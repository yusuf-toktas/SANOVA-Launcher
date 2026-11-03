using System.Globalization;
using System.Text.Json;

namespace Sanova.Core.Localization;

/// <summary>Metadata from a language file's <c>_meta</c> block.</summary>
public sealed record LanguageInfo(string Code, string EnglishName, string NativeName, string CultureName, bool IsRightToLeft = false)
{
    public CultureInfo Culture
    {
        get
        {
            try
            {
                return CultureInfo.GetCultureInfo(CultureName);
            }
            catch (CultureNotFoundException)
            {
                return CultureInfo.InvariantCulture;
            }
        }
    }
}

/// <summary>A loaded translation: metadata plus flattened "section.key" → text entries.</summary>
public sealed class LanguagePack
{
    public LanguagePack(LanguageInfo info, IReadOnlyDictionary<string, string> strings)
    {
        Info = info;
        Strings = strings;
    }

    public LanguageInfo Info { get; }

    public IReadOnlyDictionary<string, string> Strings { get; }

    /// <summary>
    /// Parses a language file. Nested objects are flattened with dots, so
    /// <c>{"home": {"play": "Play"}}</c> becomes <c>home.play</c>.
    /// </summary>
    public static LanguagePack Load(Stream json, string sourceName)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Language file '{sourceName}' must contain a JSON object.");
        }

        if (!root.TryGetProperty("_meta", out var meta) || meta.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Language file '{sourceName}' is missing its _meta block.");
        }

        var code = RequireString(meta, "code", sourceName).ToLowerInvariant();
        var info = new LanguageInfo(
            code,
            RequireString(meta, "name", sourceName),
            RequireString(meta, "nativeName", sourceName),
            meta.TryGetProperty("culture", out var culture) && culture.ValueKind == JsonValueKind.String ? culture.GetString()! : code,
            meta.TryGetProperty("rtl", out var rtl) && rtl.ValueKind == JsonValueKind.True);

        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (property.NameEquals("_meta")) continue;
            Flatten(property.Name, property.Value, strings, sourceName);
        }

        return new LanguagePack(info, strings);
    }

    private static void Flatten(string prefix, JsonElement element, Dictionary<string, string> into, string sourceName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                into[prefix] = element.GetString()!;
                break;
            case JsonValueKind.Object:
                foreach (var child in element.EnumerateObject())
                {
                    Flatten(prefix + "." + child.Name, child.Value, into, sourceName);
                }
                break;
            default:
                throw new InvalidDataException($"Language file '{sourceName}': '{prefix}' must be a string or an object.");
        }
    }

    private static string RequireString(JsonElement element, string name, string sourceName)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"Language file '{sourceName}': _meta.{name} is required.");
}
