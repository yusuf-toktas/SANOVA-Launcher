using System.Text;

namespace Sanova.Core.Game;

/// <summary>A node of Valve's text KeyValues format (libraryfolders.vdf, appmanifest_*.acf).</summary>
public sealed class KeyValuesNode
{
    public string? Value { get; init; }

    public Dictionary<string, KeyValuesNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public KeyValuesNode? this[string key] => Children.GetValueOrDefault(key);
}

/// <summary>Tolerant parser for the subset of KeyValues used by Steam's library files.</summary>
public static class KeyValuesParser
{
    public static KeyValuesNode Parse(string text)
    {
        var position = 0;
        var root = new KeyValuesNode();
        ParseObject(text, ref position, root, topLevel: true);
        return root;
    }

    private static void ParseObject(string text, ref int position, KeyValuesNode into, bool topLevel)
    {
        while (true)
        {
            SkipWhitespaceAndComments(text, ref position);
            if (position >= text.Length) return;
            if (text[position] == '}')
            {
                position++;
                if (!topLevel) return;
                continue;
            }

            var key = ReadToken(text, ref position);
            if (key is null) return;

            SkipWhitespaceAndComments(text, ref position);
            if (position >= text.Length) return;

            if (text[position] == '{')
            {
                position++;
                var child = new KeyValuesNode();
                ParseObject(text, ref position, child, topLevel: false);
                into.Children[key] = child;
            }
            else
            {
                var value = ReadToken(text, ref position);
                into.Children[key] = new KeyValuesNode { Value = value };
            }
        }
    }

    private static string? ReadToken(string text, ref int position)
    {
        if (position >= text.Length) return null;
        if (text[position] != '"')
        {
            var start = position;
            while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not '{' and not '}')
            {
                position++;
            }
            return position > start ? text[start..position] : null;
        }

        position++;
        var builder = new StringBuilder();
        while (position < text.Length && text[position] != '"')
        {
            if (text[position] == '\\' && position + 1 < text.Length)
            {
                position++;
                builder.Append(text[position] switch { 'n' => '\n', 't' => '\t', var c => c });
            }
            else
            {
                builder.Append(text[position]);
            }
            position++;
        }
        position++; // closing quote
        return builder.ToString();
    }

    private static void SkipWhitespaceAndComments(string text, ref int position)
    {
        while (position < text.Length)
        {
            if (char.IsWhiteSpace(text[position]))
            {
                position++;
            }
            else if (text[position] == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] != '\n') position++;
            }
            else
            {
                return;
            }
        }
    }
}
