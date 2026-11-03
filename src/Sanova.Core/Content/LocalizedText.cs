using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sanova.Core.Content;

/// <summary>
/// Text supplied by content authors in several languages. In JSON it is either a plain
/// string (treated as English) or an object such as <c>{"en": "...", "tr": "..."}</c>.
/// </summary>
[JsonConverter(typeof(LocalizedTextConverter))]
public sealed class LocalizedText : IReadOnlyDictionary<string, string>
{
    private readonly Dictionary<string, string> _values;

    public LocalizedText(IEnumerable<KeyValuePair<string, string>>? values = null)
    {
        _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values ?? [])
        {
            if (!string.IsNullOrWhiteSpace(key) && value is not null) _values[key.Trim().ToLowerInvariant()] = value;
        }
    }

    public static LocalizedText English(string text) => new([new("en", text)]);

    public string this[string key] => _values[key];

    public int Count => _values.Count;

    public IEnumerable<string> Keys => _values.Keys;

    public IEnumerable<string> Values => _values.Values;

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value) => _values.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>English text, or the first available translation (for logs and tools).</summary>
    public string Default => _values.TryGetValue("en", out var en) ? en : _values.Values.FirstOrDefault() ?? string.Empty;

    public override string ToString() => Default;
}

public sealed class LocalizedTextConverter : JsonConverter<LocalizedText>
{
    public override LocalizedText? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return LocalizedText.English(reader.GetString()!);
            case JsonTokenType.StartObject:
                var values = new List<KeyValuePair<string, string>>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected a language code.");
                    var key = reader.GetString()!;
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.String) throw new JsonException($"Translation '{key}' must be a string.");
                    values.Add(new(key, reader.GetString()!));
                }
                return new LocalizedText(values);
            default:
                throw new JsonException("Localized text must be a string or an object of translations.");
        }
    }

    public override void Write(Utf8JsonWriter writer, LocalizedText value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (key, text) in value.OrderBy(p => p.Key == "en" ? 0 : 1).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            writer.WriteString(key, text);
        }
        writer.WriteEndObject();
    }
}
