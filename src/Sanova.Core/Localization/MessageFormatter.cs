using System.Globalization;
using System.Text;

namespace Sanova.Core.Localization;

/// <summary>
/// Fills named placeholders such as <c>{count}</c> or <c>{size:N1}</c>. Use <c>{{</c> and
/// <c>}}</c> for literal braces. Unknown placeholders are left untouched so missing
/// arguments are visible instead of silently disappearing.
/// </summary>
public static class MessageFormatter
{
    public static string Format(string template, IReadOnlyDictionary<string, object?>? args, CultureInfo culture)
    {
        if (args is null || args.Count == 0 || template.IndexOf('{', StringComparison.Ordinal) < 0)
        {
            return template.Replace("{{", "{", StringComparison.Ordinal).Replace("}}", "}", StringComparison.Ordinal);
        }

        var result = new StringBuilder(template.Length + 16);
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    result.Append('{');
                    i++;
                    continue;
                }

                var close = template.IndexOf('}', i + 1);
                if (close < 0)
                {
                    result.Append(template, i, template.Length - i);
                    break;
                }

                var token = template.AsSpan(i + 1, close - i - 1);
                var colon = token.IndexOf(':');
                var name = (colon >= 0 ? token[..colon] : token).Trim().ToString();
                var format = colon >= 0 ? token[(colon + 1)..].ToString() : null;

                if (args.TryGetValue(name, out var value))
                {
                    result.Append(FormatValue(value, format, culture));
                }
                else
                {
                    result.Append(template, i, close - i + 1);
                }
                i = close;
            }
            else if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                result.Append('}');
                i++;
            }
            else
            {
                result.Append(c);
            }
        }
        return result.ToString();
    }

    private static string FormatValue(object? value, string? format, CultureInfo culture) => value switch
    {
        null => string.Empty,
        IFormattable formattable => formattable.ToString(format, culture),
        _ => value.ToString() ?? string.Empty,
    };
}
