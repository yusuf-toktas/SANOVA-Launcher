using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sanova.Core.Localization;

/// <summary>
/// Thread-safe string lookup with English fallback. The UI layer wraps this to raise
/// change notifications; the engine itself never produces user-facing text.
/// </summary>
public sealed class Localizer
{
    public const string FallbackLanguage = "en";

    private readonly Dictionary<string, LanguagePack> _packs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _reportedMissing = new(StringComparer.Ordinal);
    private readonly ILogger _logger;
    private volatile LanguagePack _current;
    private readonly LanguagePack _fallback;

    public Localizer(IEnumerable<LanguagePack> packs, ILogger<Localizer>? logger = null)
    {
        _logger = logger ?? NullLogger<Localizer>.Instance;
        foreach (var pack in packs)
        {
            _packs[pack.Info.Code] = pack;
        }
        if (!_packs.TryGetValue(FallbackLanguage, out var fallback))
        {
            throw new InvalidOperationException("The English language pack is required as the fallback language.");
        }
        _fallback = fallback;
        _current = fallback;
    }

    public event EventHandler? LanguageChanged;

    public IReadOnlyList<LanguageInfo> Languages => _packs.Values
        .Select(p => p.Info)
        .OrderBy(i => i.Code == FallbackLanguage ? 0 : 1)
        .ThenBy(i => i.NativeName, StringComparer.Create(CultureInfo.InvariantCulture, ignoreCase: true))
        .ToList();

    public LanguageInfo Current => _current.Info;

    public CultureInfo Culture => _current.Info.Culture;

    public bool IsAvailable(string code) => _packs.ContainsKey(code);

    /// <summary>Switches language; unknown codes fall back to English. Returns the applied code.</summary>
    public string SetLanguage(string code)
    {
        var pack = _packs.GetValueOrDefault(code) ?? _fallback;
        if (!ReferenceEquals(pack, _current))
        {
            _current = pack;
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
        return pack.Info.Code;
    }

    /// <summary>Picks the best available language for a system culture (e.g. tr-TR → tr).</summary>
    public string ResolveSystemLanguage(CultureInfo culture)
    {
        for (var c = culture; !Equals(c, CultureInfo.InvariantCulture); c = c.Parent)
        {
            if (_packs.ContainsKey(c.Name)) return _packs[c.Name].Info.Code;
            if (_packs.ContainsKey(c.TwoLetterISOLanguageName)) return _packs[c.TwoLetterISOLanguageName].Info.Code;
        }
        return FallbackLanguage;
    }

    public bool HasKey(string key) => _current.Strings.ContainsKey(key) || _fallback.Strings.ContainsKey(key);

    public string this[string key] => Get(key);

    public string Get(string key) => Get(key, (IReadOnlyDictionary<string, object?>?)null);

    public string Get(string key, IReadOnlyDictionary<string, object?>? args)
    {
        var pack = _current;
        if (!pack.Strings.TryGetValue(key, out var template) && !_fallback.Strings.TryGetValue(key, out template))
        {
            if (_reportedMissing.TryAdd(key, 0))
            {
                _logger.LogWarning("Missing localization key {Key}", key);
            }
            return key;
        }
        return MessageFormatter.Format(template, args, pack.Info.Culture);
    }

    public string Get(string key, params (string Name, object? Value)[] args) => Get(key, ToArgs(args));

    /// <summary>Plural lookup: resolves <c>key.one</c>, <c>key.other</c>, ... and adds a <c>count</c> argument.</summary>
    public string Plural(string key, long count, params (string Name, object? Value)[] args)
    {
        var all = ToArgs(args);
        all["count"] = count;
        var pack = _current;
        var category = PluralRules.Suffix(PluralRules.Select(pack.Info.Code, count));
        var specific = $"{key}.{category}";
        if (pack.Strings.ContainsKey(specific)) return Get(specific, all);
        var other = $"{key}.other";
        if (pack.Strings.ContainsKey(other)) return Get(other, all);
        // Fall back to the English rules when the current language lacks the key.
        var englishCategory = PluralRules.Suffix(PluralRules.Select(FallbackLanguage, count));
        return Get($"{key}.{englishCategory}", all);
    }

    /// <summary>Resolves manifest-provided text (e.g. mod descriptions) for the current language.</summary>
    public string Resolve(IReadOnlyDictionary<string, string>? translations)
    {
        if (translations is null || translations.Count == 0) return string.Empty;
        if (translations.TryGetValue(_current.Info.Code, out var text) && !string.IsNullOrWhiteSpace(text)) return text;
        if (translations.TryGetValue(FallbackLanguage, out text) && !string.IsNullOrWhiteSpace(text)) return text;
        return translations.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
    }

    public string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (Math.Abs(value) >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var format = unit == 0 ? "N0" : value >= 100 ? "N0" : "N1";
        return Get("format.bytes", ("value", value.ToString(format, Culture)), ("unit", units[unit]));
    }

    public string FormatSpeed(double bytesPerSecond) => Get("format.speed", ("value", FormatBytes((long)bytesPerSecond)));

    public string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        if (duration.TotalSeconds < 60) return Plural("format.seconds", (long)Math.Ceiling(duration.TotalSeconds));
        if (duration.TotalMinutes < 60) return Plural("format.minutes", (long)Math.Round(duration.TotalMinutes));
        var hours = (long)duration.TotalHours;
        var minutes = duration.Minutes;
        return minutes == 0
            ? Plural("format.hours", hours)
            : Get("format.hoursMinutes", ("hours", Plural("format.hours", hours)), ("minutes", Plural("format.minutes", minutes)));
    }

    public string FormatDate(DateTimeOffset value) => value.ToLocalTime().ToString("g", Culture);

    public string FormatRelative(DateTimeOffset value, DateTimeOffset now)
    {
        var delta = now - value;
        if (delta < TimeSpan.FromMinutes(1)) return Get("format.justNow");
        if (delta < TimeSpan.FromHours(1)) return Plural("format.minutesAgo", (long)delta.TotalMinutes);
        if (delta < TimeSpan.FromDays(1)) return Plural("format.hoursAgo", (long)delta.TotalHours);
        if (delta < TimeSpan.FromDays(7)) return Plural("format.daysAgo", (long)delta.TotalDays);
        return FormatDate(value);
    }

    private static Dictionary<string, object?> ToArgs((string Name, object? Value)[] args)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in args)
        {
            dict[name] = value;
        }
        return dict;
    }
}
