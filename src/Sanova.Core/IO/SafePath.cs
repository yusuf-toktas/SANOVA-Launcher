using System.Text.RegularExpressions;

namespace Sanova.Core.IO;

/// <summary>
/// Validation and resolution of relative paths that come from untrusted sources
/// (manifests, archives, configuration). A path accepted here can never escape the
/// root it is resolved against, alias another file, or address a device.
/// </summary>
public static partial class SafePath
{
    public const int MaxRelativeLength = 240;

    /// <summary>Windows file systems are case-insensitive; compare relative paths ordinally ignoring case.</summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    public static StringComparison Comparison => StringComparison.OrdinalIgnoreCase;

    private static readonly HashSet<string> ReservedNames = BuildReservedNames();

    [GeneratedRegex(@"^[^~]{1,6}~[0-9]+(\.|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ShortNamePattern();

    private static HashSet<string> BuildReservedNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" };
        foreach (var prefix in new[] { "COM", "LPT" })
        {
            for (var i = 0; i <= 9; i++) names.Add(prefix + i);
            names.Add(prefix + "\u00B9");
            names.Add(prefix + "\u00B2");
            names.Add(prefix + "\u00B3");
        }
        return names;
    }

    /// <summary>Validates and normalizes to forward slashes, or throws <see cref="SanovaException"/>.</summary>
    public static string NormalizeRelative(string? path)
    {
        if (!TryNormalizeRelative(path, out var normalized, out var reason))
        {
            throw SanovaException.Create(ErrorCodes.PathUnsafe, $"Unsafe relative path '{path}': {reason}", ("path", path));
        }
        return normalized;
    }

    public static bool TryNormalizeRelative(string? path, out string normalized, out string? reason)
    {
        normalized = string.Empty;
        reason = null;

        if (string.IsNullOrEmpty(path))
        {
            reason = "path is empty";
            return false;
        }

        var p = path.Replace('\\', '/');
        if (p.Length > MaxRelativeLength)
        {
            reason = "path is too long";
            return false;
        }
        if (p[0] == '/' || p.StartsWith("//", StringComparison.Ordinal))
        {
            reason = "path is rooted";
            return false;
        }
        if (p.EndsWith('/'))
        {
            reason = "path ends with a separator";
            return false;
        }

        foreach (var segment in p.Split('/'))
        {
            if (!IsSafeSegment(segment, out reason))
            {
                return false;
            }
        }

        normalized = p;
        return true;
    }

    private static bool IsSafeSegment(string segment, out string? reason)
    {
        reason = null;
        if (segment.Length == 0)
        {
            reason = "path contains an empty segment";
            return false;
        }
        if (segment is "." or "..")
        {
            reason = "path contains a relative segment";
            return false;
        }
        foreach (var c in segment)
        {
            if (c < 0x20 || c is '<' or '>' or ':' or '"' or '|' or '?' or '*')
            {
                reason = $"path contains the invalid character U+{(int)c:X4}";
                return false;
            }
        }
        if (char.IsWhiteSpace(segment[0]) || char.IsWhiteSpace(segment[^1]) || segment[^1] == '.')
        {
            reason = "segment starts/ends with whitespace or ends with a dot";
            return false;
        }

        var dot = segment.IndexOf('.', StringComparison.Ordinal);
        var stem = dot >= 0 ? segment[..dot] : segment;
        if (ReservedNames.Contains(stem.TrimEnd()))
        {
            reason = "segment is a reserved device name";
            return false;
        }
        if (ShortNamePattern().IsMatch(segment))
        {
            reason = "segment looks like an 8.3 short name";
            return false;
        }
        return true;
    }

    /// <summary>Returns a canonical absolute root without a trailing separator.</summary>
    public static string NormalizeRoot(string root)
    {
        var full = Path.GetFullPath(root);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Keep "C:\" as-is: trimming would turn it into the drive-relative "C:".
        return trimmed.EndsWith(':') ? full : trimmed;
    }

    /// <summary>Resolves <paramref name="relative"/> under <paramref name="root"/>, guaranteeing containment.</summary>
    public static string GetFullPathUnder(string root, string relative)
    {
        var normalized = NormalizeRelative(relative);
        var normalizedRoot = NormalizeRoot(root);
        var full = Path.GetFullPath(Path.Combine(normalizedRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsStrictlyUnder(normalizedRoot, full))
        {
            throw SanovaException.Create(ErrorCodes.PathUnsafe, $"Path '{relative}' escapes '{root}'.", ("path", relative));
        }
        return full;
    }

    /// <summary>True when <paramref name="candidate"/> is inside <paramref name="root"/> (not equal to it).</summary>
    public static bool IsStrictlyUnder(string root, string candidate)
    {
        var normalizedRoot = NormalizeRoot(root);
        var full = Path.GetFullPath(candidate);
        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar) ? normalizedRoot : normalizedRoot + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, Comparison) && full.Length > prefix.Length;
    }

    /// <summary>True when the paths refer to the same location, or one contains the other.</summary>
    public static bool Overlaps(string a, string b)
    {
        var na = NormalizeRoot(a);
        var nb = NormalizeRoot(b);
        return string.Equals(na, nb, Comparison) || IsStrictlyUnder(na, nb) || IsStrictlyUnder(nb, na);
    }

    public static bool AreSame(string a, string b) => string.Equals(NormalizeRoot(a), NormalizeRoot(b), Comparison);

    /// <summary>Converts an absolute path under <paramref name="root"/> into a normalized relative path.</summary>
    public static string GetRelative(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(NormalizeRoot(root), Path.GetFullPath(fullPath));
        return NormalizeRelative(relative);
    }
}
