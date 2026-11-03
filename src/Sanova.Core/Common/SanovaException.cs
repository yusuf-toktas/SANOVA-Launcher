namespace Sanova.Core;

/// <summary>
/// An expected, user-explainable failure. The <see cref="Code"/> maps to a localized
/// message (<c>error.&lt;code&gt;</c>) in the UI; <see cref="Args"/> fills its placeholders.
/// The exception message itself is English and intended for logs only.
/// </summary>
public class SanovaException : Exception
{
    private static readonly IReadOnlyDictionary<string, object?> NoArgs = new Dictionary<string, object?>();

    public SanovaException(string code, string message, IReadOnlyDictionary<string, object?>? args = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Args = args ?? NoArgs;
    }

    public string Code { get; }

    public IReadOnlyDictionary<string, object?> Args { get; }

    public static SanovaException Create(string code, string message, params (string Key, object? Value)[] args)
        => new(code, message, ToDictionary(args));

    public static SanovaException Wrap(string code, string message, Exception inner, params (string Key, object? Value)[] args)
        => new(code, message, ToDictionary(args), inner);

    private static Dictionary<string, object?> ToDictionary((string Key, object? Value)[] args)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in args)
        {
            dict[key] = value;
        }
        return dict;
    }
}

/// <summary>Stable error codes. Each one has a matching <c>error.&lt;code&gt;</c> localization key.</summary>
public static class ErrorCodes
{
    public const string Unexpected = "unexpected";
    public const string Cancelled = "cancelled";

    public const string PathUnsafe = "path_unsafe";
    public const string PathNotWritable = "path_not_writable";
    public const string PathInsideGame = "path_inside_game";
    public const string PathNotEmpty = "path_not_empty";
    public const string PathSystemFolder = "path_system_folder";
    public const string DiskSpace = "disk_space";

    public const string GameNotFound = "game_not_found";
    public const string GameIncompatible = "game_incompatible";
    public const string GameRunning = "game_running";
    public const string GameExecutableMissing = "game_exe_missing";
    public const string GameLaunchFailed = "game_launch_failed";
    public const string GameLaunchElevation = "game_launch_elevation";

    public const string ManifestInvalid = "manifest_invalid";
    public const string ManifestSchemaUnsupported = "manifest_schema";
    public const string ManifestSignatureInvalid = "manifest_signature_invalid";
    public const string ManifestSignatureMissing = "manifest_signature_missing";
    public const string LauncherTooOld = "launcher_too_old";

    public const string SourceNotConfigured = "source_not_configured";
    public const string SourceUnreachable = "source_unreachable";
    public const string SourceRateLimited = "source_rate_limited";
    public const string SourceNoRelease = "source_no_release";
    public const string SourceHttpError = "source_http_error";
    public const string SourceInsecure = "source_insecure";

    public const string DownloadFailed = "download_failed";
    public const string DownloadHashMismatch = "download_hash_mismatch";
    public const string DownloadSizeMismatch = "download_size_mismatch";

    public const string ArchiveInvalid = "archive_invalid";
    public const string ArchiveUnsafeEntry = "archive_unsafe_entry";
    public const string ArchiveEntryMissing = "archive_entry_missing";
    public const string ArchiveTooLarge = "archive_too_large";

    public const string NotInstalled = "not_installed";
    public const string InstallBusy = "install_busy";
    public const string InstallLocked = "install_locked";
    public const string InstallStateCorrupt = "install_state_corrupt";
    public const string RollbackFailed = "rollback_failed";
    public const string BaseSourceMissing = "base_source_missing";
    public const string ManualDownloadRequired = "manual_download_required";
    public const string FileVerificationFailed = "file_verification_failed";

    public const string ComponentDependency = "component_dependency";
    public const string ComponentConflict = "component_conflict";
    public const string ComponentRequired = "component_required";

    public const string SavesBackupFailed = "saves_backup_failed";
    public const string SavesRestoreFailed = "saves_restore_failed";

    public const string AccessDenied = "access_denied";
    public const string FileInUse = "file_in_use";
    public const string IoError = "io_error";
}
