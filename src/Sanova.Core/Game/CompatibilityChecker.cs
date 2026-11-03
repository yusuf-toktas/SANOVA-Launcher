using Microsoft.Extensions.Logging;
using Sanova.Core.Installation;
using Sanova.Core.IO;

namespace Sanova.Core.Game;

public enum FindingSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// One result of a compatibility check. <see cref="Code"/> maps to the localization keys
/// <c>compat.&lt;code&gt;.title</c> and <c>compat.&lt;code&gt;.body</c>.
/// </summary>
public sealed record CompatibilityFinding(
    FindingSeverity Severity,
    string Code,
    IReadOnlyDictionary<string, object?> Args,
    IReadOnlyList<string> Items,
    bool Overridable = false);

public enum CompatibilityVerdict
{
    Compatible,
    CompatibleWithWarnings,
    Incompatible,
}

public sealed record CompatibilityReport(
    string GameDirectory,
    ExecutableIdentity Executable,
    IReadOnlyList<CompatibilityFinding> Findings,
    BaseGameReference? Reference,
    bool AllowUnsupported)
{
    public CompatibilityVerdict Verdict =>
        Findings.Any(f => f.Severity == FindingSeverity.Error) ? CompatibilityVerdict.Incompatible
        : Findings.Any(f => f.Severity == FindingSeverity.Warning) ? CompatibilityVerdict.CompatibleWithWarnings
        : CompatibilityVerdict.Compatible;

    /// <summary>True when installation may continue, honoring the "allow unsupported versions" override.</summary>
    public bool CanProceed => Findings
        .Where(f => f.Severity == FindingSeverity.Error)
        .All(f => AllowUnsupported && f.Overridable);

    public bool UsedOverride => AllowUnsupported && Findings.Any(f => f.Severity == FindingSeverity.Error);
}

public sealed record CompatibilityCheckOptions(
    bool AllowUnsupported = false,
    CompatibilityRules? Rules = null,
    BaseGameReference? ManifestReference = null);

/// <summary>Decides whether a folder can serve as the source for a SANOVA installation, and explains why not.</summary>
public sealed class CompatibilityChecker
{
    private static readonly string[] ModFolders = ["modloader", "cleo", "scripts", "moonloader", "sampfuncs", "enbseries", "reshade-shaders"];
    private static readonly string[] ModFiles =
    [
        "dinput8.dll", "d3d8.dll", "d3d9.dll", "dxgi.dll", "vorbisHooked.dll", "samp.dll", "samp.exe", "bass.dll",
        "enbseries.ini", "enblocal.ini", "ReShade.ini", "modloader.ini",
    ];

    /// <summary>Size of the original vorbisFile.dll; ASI loaders commonly replace it.</summary>
    private const long OriginalVorbisFileSize = 65536;

    private readonly GameVersionDatabase _database;
    private readonly IReadOnlyList<BaseGameReference> _embeddedReferences;
    private readonly ILogger<CompatibilityChecker> _logger;

    public CompatibilityChecker(GameVersionDatabase database, IReadOnlyList<BaseGameReference> embeddedReferences, ILogger<CompatibilityChecker> logger)
    {
        _database = database;
        _embeddedReferences = embeddedReferences;
        _logger = logger;
    }

    public async Task<CompatibilityReport> CheckAsync(string gameDirectory, CompatibilityCheckOptions options, CancellationToken cancellationToken = default)
    {
        var findings = new List<CompatibilityFinding>();
        var database = _database.WithRules(options.Rules);

        if (!Directory.Exists(gameDirectory))
        {
            findings.Add(Finding(FindingSeverity.Error, "folder_missing", ("path", gameDirectory)));
            return new CompatibilityReport(gameDirectory, ExecutableIdentity.Missing, findings, null, options.AllowUnsupported);
        }

        if (InstallStateStore.Exists(gameDirectory))
        {
            findings.Add(Finding(FindingSeverity.Error, "folder_is_sanova"));
            return new CompatibilityReport(gameDirectory, ExecutableIdentity.Missing, findings, null, options.AllowUnsupported);
        }

        ExecutableIdentity identity;
        try
        {
            identity = await ExecutableIdentifier.IdentifyAsync(gameDirectory, database, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the game executable in {Directory}", gameDirectory);
            findings.Add(Finding(FindingSeverity.Error, "folder_no_access", ("path", gameDirectory)));
            return new CompatibilityReport(gameDirectory, ExecutableIdentity.Missing, findings, null, options.AllowUnsupported);
        }

        AddExecutableFindings(identity, findings);

        BaseGameReference? reference = null;
        if (identity.Kind is not (ExecutableKind.Missing or ExecutableKind.DefinitiveEdition))
        {
            reference = SelectReference(identity, options.ManifestReference);
            AddFileFindings(gameDirectory, reference, findings);
            AddModFindings(gameDirectory, reference, findings);
        }

        _logger.LogInformation(
            "Compatibility check for {Directory}: executable {Kind} {Id}, {Errors} error(s), {Warnings} warning(s)",
            gameDirectory, identity.Kind, identity.Match?.Id ?? "-",
            findings.Count(f => f.Severity == FindingSeverity.Error), findings.Count(f => f.Severity == FindingSeverity.Warning));

        return new CompatibilityReport(gameDirectory, identity, findings, reference, options.AllowUnsupported);
    }

    public BaseGameReference? SelectReference(ExecutableIdentity identity, BaseGameReference? manifestReference)
    {
        var id = identity.Match?.Id;
        if (manifestReference is not null && manifestReference.Files.Count > 0 && manifestReference.AppliesTo(id))
        {
            return manifestReference;
        }
        return id is null ? null : _embeddedReferences.FirstOrDefault(r => r.ExecutableIds.Count > 0 && r.AppliesTo(id));
    }

    private static void AddExecutableFindings(ExecutableIdentity identity, List<CompatibilityFinding> findings)
    {
        switch (identity.Kind)
        {
            case ExecutableKind.DefinitiveEdition:
                findings.Add(Finding(FindingSeverity.Error, "definitive_edition"));
                return;
            case ExecutableKind.Missing:
                findings.Add(Finding(FindingSeverity.Error, "exe_missing"));
                return;
            case ExecutableKind.ModernRerelease:
                findings.Add(OverridableError("exe_rerelease"));
                return;
            case ExecutableKind.RetailUnrecognized:
                findings.Add(OverridableError("exe_retail_unrecognized", ("size", identity.Size)));
                return;
            case ExecutableKind.Unknown:
                findings.Add(OverridableError("exe_unknown", ("size", identity.Size), ("sha256", identity.Sha256)));
                return;
        }

        var match = identity.Match!;
        (string, object?)[] args = [("version", match.Version ?? "?"), ("region", match.Region ?? "?"), ("label", match.Label)];
        switch (match.Support)
        {
            case ExecutableSupport.Supported:
                findings.Add(Finding(FindingSeverity.Info, "exe_supported", args));
                break;
            case ExecutableSupport.Unverified:
                findings.Add(Finding(FindingSeverity.Warning, match.Edition == ExecutableEdition.Modified ? "exe_modified" : "exe_unverified", args));
                break;
            default:
                findings.Add(OverridableError("exe_unsupported", args));
                break;
        }

        if (match.Notes.Contains("securom", StringComparer.OrdinalIgnoreCase))
        {
            findings.Add(Finding(FindingSeverity.Info, "exe_disc_check"));
        }
    }

    private static void AddFileFindings(string gameDirectory, BaseGameReference? reference, List<CompatibilityFinding> findings)
    {
        var missing = new List<string>();
        var modified = new List<string>();

        if (reference is not null)
        {
            foreach (var file in reference.Files)
            {
                var full = Path.Combine(gameDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar));
                var info = new FileInfo(full);
                if (!info.Exists) missing.Add(file.Path);
                else if (info.Length != file.Size) modified.Add(file.Path);
            }
        }
        else
        {
            missing.AddRange(BaseGameReference.MinimalRequiredFiles.Where(relative =>
                !File.Exists(Path.Combine(gameDirectory, relative.Replace('/', Path.DirectorySeparatorChar)))));
        }

        if (missing.Count > 0)
        {
            findings.Add(new CompatibilityFinding(FindingSeverity.Error, "files_missing", Args(("count", missing.Count)), missing));
        }
        if (modified.Count > 0)
        {
            findings.Add(new CompatibilityFinding(FindingSeverity.Warning, "files_modified", Args(("count", modified.Count)), modified));
        }
    }

    private static void AddModFindings(string gameDirectory, BaseGameReference? reference, List<CompatibilityFinding> findings)
    {
        var detected = new List<string>();
        foreach (var folder in ModFolders)
        {
            if (Directory.Exists(Path.Combine(gameDirectory, folder))) detected.Add(folder + "/");
        }
        foreach (var file in ModFiles)
        {
            if (File.Exists(Path.Combine(gameDirectory, file))) detected.Add(file);
        }
        try
        {
            detected.AddRange(Directory.EnumerateFiles(gameDirectory, "*.asi").Select(Path.GetFileName)!);
            var vorbis = new FileInfo(Path.Combine(gameDirectory, "vorbisFile.dll"));
            if (vorbis.Exists && vorbis.Length != OriginalVorbisFileSize) detected.Add("vorbisFile.dll");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable folder contents are reported by other checks.
        }

        if (detected.Count > 0)
        {
            var code = reference is not null ? "mods_detected_filtered" : "mods_detected";
            findings.Add(new CompatibilityFinding(FindingSeverity.Warning, code, Args(("count", detected.Count)), detected.Distinct(SafePath.Comparer).ToList()));
        }
    }

    private static CompatibilityFinding Finding(FindingSeverity severity, string code, params (string Key, object? Value)[] args)
        => new(severity, code, Args(args), []);

    /// <summary>An executable-version error the "allow unsupported game versions" setting may override.</summary>
    private static CompatibilityFinding OverridableError(string code, params (string Key, object? Value)[] args)
        => new(FindingSeverity.Error, code, Args(args), [], Overridable: true);

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] args)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in args) dict[key] = value;
        return dict;
    }
}
