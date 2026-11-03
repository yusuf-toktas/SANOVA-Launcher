using Microsoft.Extensions.Logging;
using Sanova.Core.Content;
using Sanova.Core.Game;
using Sanova.Core.IO;
using Sanova.Core.Updates;

namespace Sanova.Core.Installation;

public sealed record ApplyOptions(
    IReadOnlyDictionary<string, string>? ManualArchives = null,
    bool KeepDownloadCache = false,
    int MaxParallelDownloads = 3,
    string Channel = "stable");

public sealed class InstallRequest
{
    public required string OriginalGamePath { get; init; }

    public required string InstallPath { get; init; }

    public required ResolvedManifest Manifest { get; init; }

    public required IReadOnlySet<string> Components { get; init; }

    public required CompatibilityReport Compatibility { get; init; }

    public ApplyOptions Options { get; init; } = new();
}

public sealed record PathValidation(
    bool IsValid,
    string? ErrorCode,
    IReadOnlyDictionary<string, object?> Args,
    bool IsExistingInstallation,
    long? FreeBytes)
{
    public static PathValidation Fail(string code, params (string Key, object? Value)[] args)
        => new(false, code, args.ToDictionary(a => a.Key, a => a.Value), false, null);
}

public sealed record PlanSummary(int Added, int Updated, int Removed, int Disabled, int Enabled, long DownloadBytes, long AcquireBytes)
{
    public bool IsEmpty => Added + Updated + Removed + Disabled + Enabled == 0;

    public static PlanSummary From(InstallPlan plan)
        => new(plan.Added, plan.Updated, plan.Removed, plan.Disabled, plan.Enabled, plan.DownloadEstimate, plan.AcquireBytes);
}

public sealed record UninstallResult(int RemovedFiles, IReadOnlyList<string> KeptModified, IReadOnlyList<string> KeptUnknown, bool DirectoryRemoved, string? PreservedPlayerFiles);

/// <summary>
/// Orchestrates complete installation workflows on top of the planner, acquirer, base copier
/// and transaction engine. Every mutating workflow holds the installation lock and first
/// recovers any interrupted transaction.
/// </summary>
public sealed class InstallationService
{
    private const long SpaceMargin = 256L * 1024 * 1024;
    public const string PreservedFilesFolderName = "SANOVA - preserved player files";

    private readonly ContentAcquirer _acquirer;
    private readonly BaseGameCopier _copier;
    private readonly TransactionEngine _engine;
    private readonly ILogger<InstallationService> _logger;
    private readonly string _launcherVersion;
    private readonly Func<ManifestTrustConfig> _trustProvider;

    public InstallationService(ContentAcquirer acquirer, BaseGameCopier copier, TransactionEngine engine, ILogger<InstallationService> logger, string launcherVersion,
        Func<ManifestTrustConfig>? trustProvider = null)
    {
        _acquirer = acquirer;
        _copier = copier;
        _engine = engine;
        _logger = logger;
        _launcherVersion = launcherVersion;
        _trustProvider = trustProvider ?? (() => new ManifestTrustConfig());
    }

    public InstallState? GetState(string installDirectory) => InstallStateStore.Load(installDirectory);

    public ResolvedManifest? GetInstalledManifest(string installDirectory) => ResolvedManifest.TryLoad(InstallStateStore.ManifestDirectory(installDirectory), _trustProvider());

    /// <summary>Space needed for a fresh installation: original files, content, and the temporary download volume.</summary>
    public static long EstimateRequiredBytes(string originalGamePath, BaseGameReference? reference, ResolvedManifest manifest, IReadOnlySet<string> components)
    {
        long baseBytes;
        try
        {
            baseBytes = BaseGameCopier.SelectFiles(originalGamePath, reference).Sum(f => f.Size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            baseBytes = reference?.TotalSize ?? 0;
        }
        var files = manifest.Manifest.FilesOf(components).ToList();
        return baseBytes + files.Sum(f => f.Size) + files.Sum(f => f.PackedSize ?? f.Size) + SpaceMargin;
    }

    public PathValidation ValidateInstallPath(string installPath, string? originalGamePath, long requiredBytes)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Path.IsPathFullyQualified(installPath))
        {
            return PathValidation.Fail(ErrorCodes.PathUnsafe, ("path", installPath));
        }

        string full;
        try
        {
            full = SafePath.NormalizeRoot(installPath);
            SafePath.RejectReparsePoints(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException || ex is SanovaException { Code: ErrorCodes.PathUnsafe })
        {
            return PathValidation.Fail(ErrorCodes.PathUnsafe, ("path", installPath));
        }

        if (full.Length > 150)
        {
            return PathValidation.Fail(ErrorCodes.PathUnsafe, ("path", full));
        }
        if (!PathEncoding.IsAnsiCompatible(full))
        {
            return PathValidation.Fail("path_not_ansi", ("path", full));
        }
        if (FileSystem.IsProtectedLocation(full) || SafePath.Overlaps(full, AppContext.BaseDirectory))
        {
            return PathValidation.Fail(ErrorCodes.PathSystemFolder, ("path", full));
        }
        if (originalGamePath is not null && SafePath.Overlaps(full, originalGamePath))
        {
            return PathValidation.Fail(ErrorCodes.PathInsideGame, ("path", full));
        }

        var existing = InstallStateStore.Exists(full);
        if (!existing && Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
        {
            return PathValidation.Fail(ErrorCodes.PathNotEmpty, ("path", full));
        }
        if (!FileSystem.IsWritable(full))
        {
            return PathValidation.Fail(ErrorCodes.PathNotWritable, ("path", full));
        }

        var free = FileSystem.GetAvailableFreeSpace(full);
        if (!existing && free is { } available && available < requiredBytes)
        {
            return new PathValidation(false, ErrorCodes.DiskSpace,
                new Dictionary<string, object?> { ["required"] = requiredBytes, ["available"] = available }, false, available);
        }
        return new PathValidation(true, null, new Dictionary<string, object?>(), existing, free);
    }

    public RecoveryOutcome Recover(string installDirectory)
    {
        if (!InstallStateStore.HasPendingTransaction(installDirectory)) return RecoveryOutcome.NothingToRecover;
        using var installLock = InstallLock.Acquire(installDirectory);
        var outcome = _engine.Recover(installDirectory);
        _logger.LogWarning("Recovered interrupted transaction in {Directory}: {Outcome}", installDirectory, outcome);
        return outcome;
    }

    public async Task InstallAsync(InstallRequest request, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var installDirectory = SafePath.NormalizeRoot(request.InstallPath);
        if (!request.Compatibility.CanProceed)
        {
            throw SanovaException.Create(ErrorCodes.GameIncompatible, "The selected game installation is not compatible.");
        }

        Directory.CreateDirectory(installDirectory);
        using var installLock = InstallLock.Acquire(installDirectory);
        _engine.Recover(installDirectory);

        var state = InstallStateStore.Load(installDirectory) ?? new InstallState
        {
            OriginalGamePath = SafePath.NormalizeRoot(request.OriginalGamePath),
            ExecutableId = request.Compatibility.Executable.Match?.Id,
            ExecutableName = request.Compatibility.Executable.FileName ?? ExecutableIdentifier.ClassicExecutable,
            LauncherVersion = _launcherVersion,
            Channel = request.Options.Channel,
        };
        InstallStateStore.Save(installDirectory, state);
        _logger.LogInformation("Installing SANOVA {Version} into {Directory} (phase {Phase})", request.Manifest.Manifest.ContentVersion, installDirectory, state.Phase);

        if (state.Phase == InstallPhase.CopyingBase)
        {
            progress?.Report(new OperationProgress(OperationStage.CopyingGameFiles));
            var copy = await _copier.CopyAsync(request.OriginalGamePath, installDirectory, request.Compatibility.Reference, progress, cancellationToken).ConfigureAwait(false);
            foreach (var (path, record) in copy.Files)
            {
                state.Files[path] = record;
            }
            state.Phase = InstallPhase.BaseReady;
            InstallStateStore.Save(installDirectory, state);
        }

        await ApplyAsync(installDirectory, state, request.Manifest, request.Components, PlanMode.Install, null, request.Options, progress, cancellationToken)
            .ConfigureAwait(false);
        await VerifyLockedAsync(installDirectory, VerifyMode.Quick, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Plans an update or component change without applying it.</summary>
    public PlanSummary Preview(string installDirectory, ResolvedManifest manifest, IReadOnlySet<string>? components)
    {
        var state = LoadReady(installDirectory);
        var selection = components ?? ReconcileSelection(manifest, state, GetInstalledManifest(installDirectory));
        var plan = InstallPlanner.Plan(installDirectory, state, manifest, selection, PlanMode.Update, "preview", null, _launcherVersion, state.Channel ?? "stable");
        return PlanSummary.From(plan);
    }

    public async Task<PlanSummary> UpdateAsync(string installDirectory, ResolvedManifest manifest, IReadOnlySet<string>? components, ApplyOptions options,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        using var installLock = InstallLock.Acquire(installDirectory);
        _engine.Recover(installDirectory);
        var state = LoadReady(installDirectory);
        var selection = components ?? ReconcileSelection(manifest, state, GetInstalledManifest(installDirectory));
        var plan = await ApplyAsync(installDirectory, state, manifest, selection, PlanMode.Update, null, options, progress, cancellationToken).ConfigureAwait(false);
        return PlanSummary.From(plan);
    }

    /// <summary>Enables/disables optional components using the manifest the installation was built from.</summary>
    public async Task<PlanSummary> ApplyComponentChangesAsync(string installDirectory, IReadOnlySet<string> components, ApplyOptions options,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        using var installLock = InstallLock.Acquire(installDirectory);
        _engine.Recover(installDirectory);
        var state = LoadReady(installDirectory);
        var manifest = GetInstalledManifest(installDirectory)
                       ?? throw SanovaException.Create(ErrorCodes.NotInstalled, "The installed manifest is missing; run an update first.");
        var plan = await ApplyAsync(installDirectory, state, manifest, components, PlanMode.Components, null, options, progress, cancellationToken).ConfigureAwait(false);
        return PlanSummary.From(plan);
    }

    public async Task<VerificationReport> VerifyAsync(string installDirectory, VerifyMode mode, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        using var installLock = InstallLock.Acquire(installDirectory);
        _engine.Recover(installDirectory);
        return await VerifyLockedAsync(installDirectory, mode, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Verifies every file and restores whatever is missing or damaged.</summary>
    public async Task<VerificationReport> RepairAsync(string installDirectory, ResolvedManifest? manifest, ApplyOptions options,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        using var installLock = InstallLock.Acquire(installDirectory);
        _engine.Recover(installDirectory);
        var report = await VerifyLockedAsync(installDirectory, VerifyMode.Full, progress, cancellationToken).ConfigureAwait(false);
        if (report.Passed) return report;

        var state = LoadReady(installDirectory);
        var source = manifest is not null && string.Equals(manifest.Manifest.ContentVersion, state.ContentVersion, StringComparison.OrdinalIgnoreCase)
            ? manifest
            : GetInstalledManifest(installDirectory) ?? manifest
              ?? throw SanovaException.Create(ErrorCodes.NotInstalled, "No manifest available to repair from.");
        var selection = new HashSet<string>(state.EnabledComponents, StringComparer.OrdinalIgnoreCase);
        await ApplyAsync(installDirectory, state, source, selection, PlanMode.Repair, report.DamagedPaths, options, progress, cancellationToken).ConfigureAwait(false);
        return await VerifyLockedAsync(installDirectory, VerifyMode.Quick, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes everything SANOVA placed. Files the player changed and files SANOVA did not
    /// create are left in place; displaced player files are moved somewhere visible.
    /// </summary>
    public Task<UninstallResult> UninstallAsync(string installDirectory, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
        => Task.Run(() => Uninstall(installDirectory, progress, cancellationToken), cancellationToken);

    private UninstallResult Uninstall(string installDirectory, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var keptModified = new List<string>();
        var keptUnknown = new List<string>();
        var removed = 0;
        string? preserved = null;

        using (var installLock = InstallLock.Acquire(installDirectory))
        {
            _engine.Recover(installDirectory);
            var state = InstallStateStore.Load(installDirectory)
                        ?? throw SanovaException.Create(ErrorCodes.NotInstalled, "No SANOVA installation found.");
            cancellationToken.ThrowIfCancellationRequested();

            var index = 0;
            foreach (var (path, record) in state.Files)
            {
                index++;
                var full = SafePath.GetFullPathUnder(installDirectory, path);
                if (!File.Exists(full)) continue;
                if (InstallPlanner.IsModified(full, record))
                {
                    keptModified.Add(path);
                    continue;
                }
                FileSystem.DeleteFile(full);
                removed++;
                if (index % 25 == 0)
                {
                    progress?.Report(new OperationProgress(OperationStage.Removing, ItemsDone: index, ItemsTotal: state.Files.Count, CurrentItem: path));
                }
            }

            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in new DirectoryInfo(installDirectory).EnumerateFiles("*", options))
            {
                var relative = Path.GetRelativePath(installDirectory, file.FullName).Replace('\\', '/');
                if (relative.StartsWith(InstallStateStore.MetadataFolderName + "/", SafePath.Comparison)) continue;
                if (!state.Files.ContainsKey(relative)) keptUnknown.Add(relative);
            }

            var userBackups = InstallStateStore.MetadataPath(installDirectory, InstallStateStore.UserBackupFolder);
            if (Directory.Exists(userBackups) && Directory.EnumerateFiles(userBackups, "*", SearchOption.AllDirectories).Any())
            {
                preserved = Path.Combine(installDirectory, PreservedFilesFolderName);
                if (Directory.Exists(preserved)) preserved += $" {DateTime.Now:yyyyMMddHHmmss}";
                Directory.Move(userBackups, preserved);
            }
        }

        FileSystem.DeleteDirectory(InstallStateStore.MetadataDirectory(installDirectory));
        RemoveEmptyDirectories(installDirectory);
        var directoryRemoved = false;
        if (Directory.Exists(installDirectory) && !Directory.EnumerateFileSystemEntries(installDirectory).Any())
        {
            Directory.Delete(installDirectory);
            directoryRemoved = true;
        }

        _logger.LogInformation("Uninstalled SANOVA from {Directory}: removed {Removed}, kept {Modified} modified and {Unknown} other file(s)",
            installDirectory, removed, keptModified.Count, keptUnknown.Count);
        progress?.Report(new OperationProgress(OperationStage.Completed));
        return new UninstallResult(removed, keptModified, keptUnknown, directoryRemoved, preserved);
    }

    /// <summary>
    /// Selection to use when updating: keeps the player's choices, adds new required components
    /// and new optional components that are on by default.
    /// </summary>
    public static HashSet<string> ReconcileSelection(ResolvedManifest manifest, InstallState state, ResolvedManifest? installed)
    {
        var choices = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in manifest.Manifest.Components.Where(c => !c.Required))
        {
            var isNew = installed?.Manifest.FindComponent(component.Id) is null && state.Phase == InstallPhase.Ready;
            choices[component.Id] = isNew ? component.DefaultEnabled : state.EnabledComponents.Contains(component.Id, StringComparer.OrdinalIgnoreCase);
        }
        return ComponentResolver.InitialSelection(manifest.Manifest, choices);
    }

    private async Task<InstallPlan> ApplyAsync(string installDirectory, InstallState state, ResolvedManifest manifest, IReadOnlySet<string> components,
        PlanMode mode, IReadOnlyCollection<string>? damagedPaths, ApplyOptions options, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        EnsureLauncherSupports(manifest.Manifest);
        var problems = ComponentResolver.Problems(manifest.Manifest, components);
        if (problems.Count > 0)
        {
            throw SanovaException.Create(ErrorCodes.ComponentDependency, "Invalid component selection: " + string.Join(" ", problems), ("detail", problems[0]));
        }

        progress?.Report(new OperationProgress(OperationStage.Preparing));
        var transactionId = TransactionEngine.NewTransactionId();
        var plan = InstallPlanner.Plan(installDirectory, state, manifest, components, mode, transactionId, damagedPaths, _launcherVersion, options.Channel);
        var stagingDirectory = Path.Combine(InstallStateStore.StagingRoot(installDirectory), transactionId);
        _logger.LogInformation("{Mode} plan: +{Added} ~{Updated} -{Removed} disabled {Disabled} enabled {Enabled}, {Bytes} bytes to acquire",
            mode, plan.Added, plan.Updated, plan.Removed, plan.Disabled, plan.Enabled, plan.AcquireBytes);

        try
        {
            var free = FileSystem.GetAvailableFreeSpace(installDirectory);
            var needed = plan.AcquireBytes + plan.DownloadEstimate + SpaceMargin;
            if (plan.ToAcquire.Count > 0 && free is { } available && available < needed)
            {
                throw SanovaException.Create(ErrorCodes.DiskSpace, "Not enough free space.", ("required", needed), ("available", available));
            }

            if (plan.ToAcquire.Count > 0)
            {
                var manual = options.ManualArchives ?? new Dictionary<string, string>();
                var missing = ContentAcquirer.MissingManualArchives(manifest.Manifest, plan.ToAcquire, manual);
                if (missing.Count > 0)
                {
                    throw SanovaException.Create(ErrorCodes.ManualDownloadRequired, $"Manual download needed for '{missing[0].Id}'.",
                        ("component", missing[0].Name.Default), ("page", missing[0].Source?.PageUrl));
                }

                await _acquirer.AcquireAsync(new AcquisitionRequest
                {
                    Manifest = manifest,
                    Files = plan.ToAcquire,
                    StagingDirectory = stagingDirectory,
                    CacheDirectory = InstallStateStore.CacheDirectory(installDirectory),
                    ManualArchives = manual,
                    MaxParallelDownloads = options.MaxParallelDownloads,
                }, progress, cancellationToken).ConfigureAwait(false);

                foreach (var file in plan.ToAcquire)
                {
                    var staged = SafePath.GetFullPathUnder(stagingDirectory, file.Path);
                    plan.NewState.Files[file.NormalizedPath].LastWriteUtc = File.GetLastWriteTimeUtc(staged);
                }
            }

            if (plan.BaseRestores.Count > 0)
            {
                await StageBaseRestoresAsync(state, plan, stagingDirectory, cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new OperationProgress(OperationStage.Applying, ItemsTotal: plan.Operations.Count));
            await Task.Run(() => _engine.Commit(installDirectory, transactionId, mode.ToString().ToLowerInvariant(), plan.Operations, plan.NewState, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!InstallStateStore.HasPendingTransaction(installDirectory))
            {
                FileSystem.TryDeleteDirectory(stagingDirectory);
            }
            throw;
        }

        foreach (var path in plan.ObsoleteStashes)
        {
            FileSystem.TryDeleteFile(InstallStateStore.MetadataPath(installDirectory, $"{InstallStateStore.DisabledFolder}/{path}"));
        }
        manifest.Save(InstallStateStore.ManifestDirectory(installDirectory));
        if (!options.KeepDownloadCache)
        {
            FileSystem.TryDeleteDirectory(InstallStateStore.CacheDirectory(installDirectory));
        }
        progress?.Report(new OperationProgress(OperationStage.Completed));
        return plan;
    }

    private async Task StageBaseRestoresAsync(InstallState state, InstallPlan plan, string stagingDirectory, CancellationToken cancellationToken)
    {
        var original = state.OriginalGamePath;
        foreach (var (path, record) in plan.BaseRestores)
        {
            var source = original is null ? null : Path.Combine(original, path.Replace('/', Path.DirectorySeparatorChar));
            if (source is null || !File.Exists(source))
            {
                throw SanovaException.Create(ErrorCodes.BaseSourceMissing, $"Original file unavailable for repair: {path}", ("path", path));
            }
            var staged = SafePath.GetFullPathUnder(stagingDirectory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            string sha;
            await using (var input = FileHashing.OpenSequentialRead(source))
            await using (var output = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                (sha, _) = await FileHashing.CopyAndHashAsync(input, output, null, null, cancellationToken).ConfigureAwait(false);
            }
            if (!FileHashing.HashEquals(sha, record.Sha256))
            {
                FileSystem.TryDeleteFile(staged);
                throw SanovaException.Create(ErrorCodes.BaseSourceMissing, $"Original file changed since installation: {path}", ("path", path));
            }
            File.SetLastWriteTimeUtc(staged, File.GetLastWriteTimeUtc(source));
            plan.NewState.Files[path] = record.Copy();
            plan.NewState.Files[path].LastWriteUtc = File.GetLastWriteTimeUtc(staged);
        }
    }

    private async Task<VerificationReport> VerifyLockedAsync(string installDirectory, VerifyMode mode, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var state = InstallStateStore.Load(installDirectory)
                    ?? throw SanovaException.Create(ErrorCodes.NotInstalled, "No SANOVA installation found.");
        var report = await InstallVerifier.VerifyAsync(installDirectory, state, mode, progress, cancellationToken).ConfigureAwait(false);
        state.LastVerifiedAt = report.CheckedAt;
        state.LastVerificationPassed = report.Passed;
        InstallStateStore.Save(installDirectory, state);
        _logger.LogInformation("Verification ({Mode}) of {Directory}: {Checked} file(s), {Issues} issue(s)", mode, installDirectory, report.Checked, report.Issues.Count);
        return report;
    }

    private InstallState LoadReady(string installDirectory)
    {
        var state = InstallStateStore.Load(installDirectory);
        if (state is null || state.Phase == InstallPhase.CopyingBase)
        {
            throw SanovaException.Create(ErrorCodes.NotInstalled, "SANOVA is not fully installed in this folder.");
        }
        return state;
    }

    private void EnsureLauncherSupports(ContentManifest manifest)
    {
        if (manifest.MinLauncherVersion is null) return;
        if (SemanticVersion.TryParse(_launcherVersion, out var current) && SemanticVersion.TryParse(manifest.MinLauncherVersion, out var required) && current < required)
        {
            throw SanovaException.Create(ErrorCodes.LauncherTooOld, $"Launcher {current} is older than the required {required}.", ("required", required.ToString()));
        }
    }

    private static void RemoveEmptyDirectories(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                     .OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            catch (IOException)
            {
                // In use or not empty after all; leave it.
            }
        }
    }
}
