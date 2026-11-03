using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sanova.Core;

/// <summary>Shared JSON settings for every file the launcher reads or writes.</summary>
public static class JsonDefaults
{
    /// <summary>Human-friendly output (settings, state, manifests).</summary>
    public static JsonSerializerOptions Indented { get; } = Create(indented: true);

    /// <summary>Compact output (journals and caches written frequently).</summary>
    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = indented,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            // Keep non-ASCII text (Turkish, etc.) readable in files people may open by hand.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
