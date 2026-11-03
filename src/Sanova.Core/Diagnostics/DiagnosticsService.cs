using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sanova.Core.Game;
using Sanova.Core.Installation;
using Sanova.Core.IO;
using Sanova.Core.Settings;
using Sanova.Core.Updates;

namespace Sanova.Core.Diagnostics;

public enum CheckStatus
{
    Pass,
    Info,
    Warning,
    Fail,
    Skipped,
}

/// <summary>
/// One diagnostic result. <see cref="Code"/> maps to the localization key <c>diag.&lt;code&gt;</c>;
/// <see cref="Detail"/> is technical English text included in shared reports.
/// </summary>
public sealed record DiagnosticCheck(string Id, CheckStatus Status, string Code, IReadOnlyDictionary<string, object?> Args, string? Detail = null);

public sealed record DiagnosticContext(
    LauncherSettings Settings,
    string LauncherVersion,
    string InformationalVersion,
    DistributionConfig Distribution,
    SettingsLoadOutcome SettingsOutcome,
    CompatibilityRules? Rules = null,
    BaseGameReference? ManifestReference = null);

/// <summary>Runs the self-checks behind the Diagnostics page and the shareable report.</summary>
public sealed class DiagnosticsService
{
    private const long LowSpaceThreshold = 2L * 1024 * 1024 * 1024;

    private readonly AppPaths _paths;
    private readonly CompatibilityChecker _compatibility;
    private readonly GameProcessMonitor _processes;
    private readonly HttpClient _http;
    private readonly ILogger<DiagnosticsService> _logger;

    public DiagnosticsService(AppPaths paths, CompatibilityChecker compatibility, GameProcessMonitor processes, HttpClient apiHttp, ILogger<DiagnosticsService> logger)
    {
        _paths = paths;
        _compatibility = compatibility;
        _processes = processes;
        _http = apiHttp;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DiagnosticCheck>> RunAsync(DiagnosticContext context, CancellationToken cancellationToken)
    {
        var checks = new List<DiagnosticCheck>
        {
            CheckOperatingSystem(),
            Info("runtime", "runtime", ("runtime", RuntimeInformation.FrameworkDescription), ("arch", RuntimeInformation.ProcessArchitecture.ToString())),
            Info("launcher", "launcher_version", ("version", context.LauncherVersion)),
            FileSystem.IsWritable(_paths.Root)
                ? Pass("data", "data_writable", ("path", _paths.Root))
                : Fail("data", "data_not_writable", ("path", _paths.Root)),
            context.SettingsOutcome == SettingsLoadOutcome.RecoveredFromCorruption
                ? Warn("settings", "settings_recovered")
                : Pass("settings", "settings_ok"),
        };

        checks.Add(await CheckOriginalGameAsync(context, cancellationToken).ConfigureAwait(false));
        checks.AddRange(await CheckInstallationAsync(context.Settings, cancellationToken).ConfigureAwait(false));

        var running = _processes.Poll();
        checks.Add(running.IsAnyRunning
            ? Info("process", "game_running", ("count", running.Processes.Count))
            : Pass("process", "game_not_running"));

        checks.AddRange(await CheckContentSourceAsync(context, cancellationToken).ConfigureAwait(false));
        checks.Add(context.Distribution.LauncherUpdates.IsConfigured
            ? Info("launcher_source", "launcher_source", ("source", context.Distribution.LauncherUpdates.Repository))
            : Warn("launcher_source", "launcher_source_not_configured"));

        _logger.LogInformation("Diagnostics finished: {Fails} failure(s), {Warnings} warning(s)",
            checks.Count(c => c.Status == CheckStatus.Fail), checks.Count(c => c.Status == CheckStatus.Warning));
        return checks;
    }

    private static DiagnosticCheck CheckOperatingSystem()
    {
        var version = Environment.OSVersion.Version;
        var description = RuntimeInformation.OSDescription;
        if (!OperatingSystem.IsWindows())
        {
            return Warn("os", "os_unsupported", ("os", description));
        }
        return OperatingSystem.IsWindowsVersionAtLeast(10)
            ? Pass("os", "os_ok", ("os", description), ("build", version.Build))
            : Warn("os", "os_old", ("os", description));
    }

    private async Task<DiagnosticCheck> CheckOriginalGameAsync(DiagnosticContext context, CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        if (string.IsNullOrWhiteSpace(settings.OriginalGamePath))
        {
            return Warn("game", "game_not_configured");
        }
        try
        {
            // Same rules as the rest of the launcher, so diagnostics never disagree with Home.
            var options = new CompatibilityCheckOptions(settings.AllowUnsupportedGameVersions, context.Rules, context.ManifestReference);
            var report = await _compatibility.CheckAsync(settings.OriginalGamePath, options, cancellationToken).ConfigureAwait(false);
            var exe = report.Executable;
            var detail = $"{exe.FileName} size={exe.Size} sha256={exe.Sha256} md5={exe.Md5} match={exe.Match?.Id ?? exe.Kind.ToString()}; " +
                         string.Join("; ", report.Findings.Select(f => $"{f.Severity}:{f.Code}"));
            // "exe" lets the UI describe the version in the player's language; "version" is the English fallback.
            var args = new (string, object?)[] { ("path", settings.OriginalGamePath), ("version", exe.Match?.Label ?? exe.Kind.ToString()), ("exe", exe) };
            return report.Verdict switch
            {
                CompatibilityVerdict.Compatible => Pass("game", "game_compatible", args) with { Detail = detail },
                CompatibilityVerdict.CompatibleWithWarnings => Warn("game", "game_warnings", args) with { Detail = detail },
                _ => Fail("game", "game_incompatible", args) with { Detail = detail },
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("game", "game_unreadable", ("path", settings.OriginalGamePath)) with { Detail = ex.Message };
        }
    }

    private async Task<IEnumerable<DiagnosticCheck>> CheckInstallationAsync(LauncherSettings settings, CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticCheck>();
        var path = settings.InstallPath;
        if (string.IsNullOrWhiteSpace(path) || !InstallStateStore.Exists(path))
        {
            results.Add(Info("install", "install_none"));
            return results;
        }

        results.Add(PathEncoding.IsAnsiCompatible(path)
            ? Pass("install_path", "install_path_ok", ("path", path))
            : Fail("install_path", "install_path_not_ansi", ("path", path)));

        if (InstallStateStore.HasPendingTransaction(path))
        {
            results.Add(Warn("transaction", "transaction_pending"));
        }

        try
        {
            var state = InstallStateStore.Load(path)!;
            var report = await InstallVerifier.VerifyAsync(path, state, VerifyMode.Quick, null, cancellationToken).ConfigureAwait(false);
            var args = new (string, object?)[] { ("version", state.ContentVersion ?? "-"), ("files", report.Checked), ("issues", report.Issues.Count) };
            var detail = report.Passed ? null : string.Join(", ", report.Issues.Take(20).Select(i => $"{i.Problem}:{i.Path}"));
            results.Add(state.Phase != InstallPhase.Ready
                ? Warn("install", "install_incomplete", args)
                : report.Passed
                    ? Pass("install", "install_ok", args)
                    : Fail("install", "install_damaged", args) with { Detail = detail });
        }
        catch (SanovaException ex)
        {
            results.Add(Fail("install", "install_state_corrupt") with { Detail = ex.Message });
        }

        var free = FileSystem.GetAvailableFreeSpace(path);
        if (free is { } bytes)
        {
            results.Add(bytes < LowSpaceThreshold ? Warn("disk", "disk_low", ("free", bytes)) : Pass("disk", "disk_ok", ("free", bytes)));
        }
        return results;
    }

    private async Task<IEnumerable<DiagnosticCheck>> CheckContentSourceAsync(DiagnosticContext context, CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticCheck>();
        var source = context.Distribution.EffectiveContentSource(context.Settings);
        if (!source.IsConfigured)
        {
            results.Add(Warn("source", "source_not_configured"));
            return results;
        }

        results.Add(Info("source", "source_configured", ("type", source.Type.ToString()), ("source", source.Repository ?? source.ManifestUrl ?? source.LocalFolder)));
        if (source.Type == ContentSourceType.LocalFolder)
        {
            results.Add(Directory.Exists(source.LocalFolder) ? Pass("network", "local_source_ok") : Fail("network", "local_source_missing", ("path", source.LocalFolder)));
            return results;
        }

        var probe = source.Type == ContentSourceType.GitHub ? new Uri("https://api.github.com/") : new Uri(source.ManifestUrl!);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, probe);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            results.Add(Pass("network", "network_ok", ("host", probe.Host)));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            results.Add(Fail("network", "network_failed", ("host", probe.Host)) with { Detail = ex.Message });
        }
        return results;
    }

    private static DiagnosticCheck Make(string id, CheckStatus status, string code, (string Key, object? Value)[] args)
        => new(id, status, code, args.ToDictionary(a => a.Key, a => a.Value));

    private static DiagnosticCheck Pass(string id, string code, params (string Key, object? Value)[] args) => Make(id, CheckStatus.Pass, code, args);
    private static DiagnosticCheck Info(string id, string code, params (string Key, object? Value)[] args) => Make(id, CheckStatus.Info, code, args);
    private static DiagnosticCheck Warn(string id, string code, params (string Key, object? Value)[] args) => Make(id, CheckStatus.Warning, code, args);
    private static DiagnosticCheck Fail(string id, string code, params (string Key, object? Value)[] args) => Make(id, CheckStatus.Fail, code, args);
}
