using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sanova.Core.IO;

namespace Sanova.Core.Settings;

public enum SettingsLoadOutcome
{
    Loaded,
    CreatedDefaults,
    RecoveredFromCorruption,
}

/// <summary>
/// Loads and atomically persists <see cref="LauncherSettings"/>. A damaged settings file
/// is preserved next to the original for inspection and replaced by defaults rather than
/// crashing the launcher.
/// </summary>
public sealed class SettingsStore
{
    private readonly AppPaths _paths;
    private readonly ILogger<SettingsStore> _logger;
    private readonly Lock _gate = new();
    private LauncherSettings _current = new();

    public SettingsStore(AppPaths paths, ILogger<SettingsStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public event EventHandler<LauncherSettings>? Changed;

    /// <summary>A snapshot copy; mutate through <see cref="Update"/>.</summary>
    public LauncherSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current.Clone();
            }
        }
    }

    public SettingsLoadOutcome LoadOutcome { get; private set; }

    public string? CorruptBackupPath { get; private set; }

    public SettingsLoadOutcome Load()
    {
        lock (_gate)
        {
            var path = _paths.SettingsFile;
            if (!File.Exists(path))
            {
                _current = new LauncherSettings();
                LoadOutcome = SettingsLoadOutcome.CreatedDefaults;
                return LoadOutcome;
            }

            try
            {
                var loaded = AtomicFile.ReadJson<LauncherSettings>(path) ?? throw new JsonException("Settings file is empty.");
                loaded.Normalize();
                _current = loaded;
                LoadOutcome = SettingsLoadOutcome.Loaded;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or InvalidOperationException)
            {
                _logger.LogError(ex, "Settings file is unreadable; continuing with defaults");
                CorruptBackupPath = Path.Combine(Path.GetDirectoryName(path)!, $"settings.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}.json");
                try
                {
                    File.Copy(path, CorruptBackupPath, overwrite: true);
                }
                catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(copyEx, "Could not preserve the damaged settings file");
                    CorruptBackupPath = null;
                }
                _current = new LauncherSettings();
                LoadOutcome = SettingsLoadOutcome.RecoveredFromCorruption;
            }
            return LoadOutcome;
        }
    }

    public LauncherSettings Update(Action<LauncherSettings> mutate)
    {
        LauncherSettings updated;
        lock (_gate)
        {
            updated = _current.Clone();
            mutate(updated);
            updated.Normalize();
            AtomicFile.WriteJson(_paths.SettingsFile, updated);
            _current = updated;
        }
        Changed?.Invoke(this, updated.Clone());
        return updated.Clone();
    }
}
