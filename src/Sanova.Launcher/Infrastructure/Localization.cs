using System.ComponentModel;
using System.Globalization;
using Avalonia.Data;
using Microsoft.Extensions.Logging;
using Sanova.Core.Localization;
using Sanova.Core.Settings;

namespace Sanova.Launcher.Infrastructure;

/// <summary>
/// UI-side localization: owns the engine's <see cref="Localizer"/>, switches languages at
/// runtime and refreshes every live <see cref="LocalizedString"/> immediately.
/// </summary>
public sealed class LocalizationService
{
    private readonly List<WeakReference<LocalizedString>> _subscribers = [];
    private readonly Lock _gate = new();

    public LocalizationService(Localizer localizer)
    {
        Localizer = localizer;
        Instance = this;
    }

    /// <summary>Static access for the XAML <c>{l:Tr}</c> extension.</summary>
    public static LocalizationService? Instance { get; private set; }

    public Localizer Localizer { get; }

    public event EventHandler? LanguageChanged;

    public CultureInfo Culture => Localizer.Culture;

    public string CurrentCode => Localizer.Current.Code;

    public IReadOnlyList<LanguageInfo> Languages => Localizer.Languages;

    /// <summary>Loads the built-in languages plus any drop-in files from %LocalAppData%\SANOVA\Languages.</summary>
    public static LocalizationService Create(AppPaths paths, ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger<LocalizationService>();
        var packs = new Dictionary<string, LanguagePack>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(LocalizationService).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Sanova.Launcher.Localization.", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var pack = LanguagePack.Load(stream, name);
            packs[pack.Info.Code] = pack;
        }

        if (Directory.Exists(paths.LanguagesDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(paths.LanguagesDirectory, "*.json"))
            {
                try
                {
                    using var stream = File.OpenRead(file);
                    var pack = LanguagePack.Load(stream, Path.GetFileName(file));
                    packs[pack.Info.Code] = pack;
                    logger.LogInformation("Loaded drop-in language {Code} from {File}", pack.Info.Code, file);
                }
                catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or IOException)
                {
                    logger.LogWarning(ex, "Ignoring invalid language file {File}", file);
                }
            }
        }

        return new LocalizationService(new Localizer(packs.Values, loggers.CreateLogger<Localizer>()));
    }

    /// <summary>Applies a settings value: "auto" follows the Windows display language.</summary>
    public void Apply(string setting)
    {
        var code = string.Equals(setting, "auto", StringComparison.OrdinalIgnoreCase)
            ? Localizer.ResolveSystemLanguage(CultureInfo.CurrentUICulture)
            : setting;
        var previous = Localizer.Current.Code;
        Localizer.SetLanguage(code);
        if (!string.Equals(previous, Localizer.Current.Code, StringComparison.Ordinal))
        {
            RefreshAll();
        }
    }

    internal void Register(LocalizedString text)
    {
        lock (_gate)
        {
            if (_subscribers.Count % 64 == 0) _subscribers.RemoveAll(w => !w.TryGetTarget(out _));
            _subscribers.Add(new WeakReference<LocalizedString>(text));
        }
    }

    private void RefreshAll()
    {
        List<LocalizedString> alive;
        lock (_gate)
        {
            _subscribers.RemoveAll(w => !w.TryGetTarget(out _));
            alive = _subscribers.Select(w => w.TryGetTarget(out var t) ? t : null).OfType<LocalizedString>().ToList();
        }
        foreach (var text in alive) text.Refresh();
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string T(string key) => Localizer.Get(key);

    public string T(string key, params (string Name, object? Value)[] args) => Localizer.Get(key, args);

    public string T(string key, IReadOnlyDictionary<string, object?> args) => Localizer.Get(key, args);

    public string Plural(string key, long count, params (string Name, object? Value)[] args) => Localizer.Plural(key, count, args);

    public string Bytes(long bytes) => Localizer.FormatBytes(bytes);

    public string Speed(double bytesPerSecond) => Localizer.FormatSpeed(bytesPerSecond);

    public string Duration(TimeSpan duration) => Localizer.FormatDuration(duration);

    public string Date(DateTimeOffset value) => Localizer.FormatDate(value);

    public string Relative(DateTimeOffset value) => Localizer.FormatRelative(value, DateTimeOffset.UtcNow);

    public string Resolve(IReadOnlyDictionary<string, string>? text) => Localizer.Resolve(text);
}

/// <summary>
/// Text that re-evaluates itself whenever the language changes. View models expose these
/// so every message — including ones built from arguments — switches language instantly.
/// </summary>
public sealed class LocalizedString : INotifyPropertyChanged
{
    private Func<LocalizationService, string> _producer;
    private string _value = string.Empty;

    public LocalizedString(string key)
        : this(l => l.T(key))
    {
    }

    public LocalizedString(Func<LocalizationService, string> producer)
    {
        _producer = producer;
        var service = LocalizationService.Instance;
        if (service is not null)
        {
            _value = Evaluate(service);
            service.Register(this);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Value => _value;

    public static LocalizedString Empty() => new(_ => string.Empty);

    public void Set(string key) => Set(l => l.T(key));

    public void Set(Func<LocalizationService, string> producer)
    {
        _producer = producer;
        Refresh();
    }

    /// <summary>Re-evaluates the text (after a language change or a change in the data it shows).</summary>
    public void Refresh()
    {
        var service = LocalizationService.Instance;
        if (service is null) return;
        var value = Evaluate(service);
        if (string.Equals(value, _value, StringComparison.Ordinal)) return;
        _value = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
    }

    private string Evaluate(LocalizationService service)
    {
        try
        {
            return _producer(service);
        }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException or FormatException)
        {
            return string.Empty;
        }
    }

    public override string ToString() => _value;
}

/// <summary>XAML markup extension: <c>Text="{l:Tr home.play}"</c>.</summary>
public sealed class TrExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key)
    {
        Key = key;
    }

    public string Key { get; set; } = string.Empty;

    public BindingBase ProvideValue(IServiceProvider serviceProvider)
        => new ReflectionBinding(nameof(LocalizedString.Value))
        {
            Source = new LocalizedString(Key),
            Mode = BindingMode.OneWay,
            FallbackValue = Key,
        };
}
