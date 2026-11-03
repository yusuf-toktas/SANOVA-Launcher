using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Sanova.Core;

/// <summary>
/// Semantic Versioning 2.0.0 value. Parsing is lenient about a leading "v" and missing
/// minor/patch components (release tags such as "v1.2" are common); comparison follows
/// SemVer precedence rules, ignoring build metadata.
/// </summary>
public readonly struct SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    public SemanticVersion(int major, int minor, int patch, string? prerelease = null, string? build = null)
    {
        if (major < 0 || minor < 0 || patch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(major), "Version components must be non-negative.");
        }
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease ?? string.Empty;
        Build = build ?? string.Empty;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string Prerelease { get; }
    public string Build { get; }

    public bool IsPrerelease => Prerelease.Length > 0;

    public static SemanticVersion Parse(string text)
        => TryParse(text, out var version)
            ? version
            : throw new FormatException($"'{text}' is not a valid semantic version.");

    public static bool TryParse([NotNullWhen(true)] string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        var build = string.Empty;
        var plus = s.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            build = s[(plus + 1)..];
            s = s[..plus];
            if (!AreValidIdentifiers(build, allowLeadingZeros: true))
            {
                return false;
            }
        }

        var prerelease = string.Empty;
        var dash = s.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            prerelease = s[(dash + 1)..];
            s = s[..dash];
            if (!AreValidIdentifiers(prerelease, allowLeadingZeros: false))
            {
                return false;
            }
        }

        var parts = s.Split('.');
        if (parts.Length is < 1 or > 3)
        {
            return false;
        }

        Span<int> numbers = stackalloc int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0 || (part.Length > 1 && part[0] == '0') || !part.All(char.IsAsciiDigit))
            {
                return false;
            }
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new SemanticVersion(numbers[0], numbers[1], numbers[2], prerelease, build);
        return true;
    }

    private static bool AreValidIdentifiers(string value, bool allowLeadingZeros)
    {
        if (value.Length == 0)
        {
            return false;
        }
        foreach (var identifier in value.Split('.'))
        {
            if (identifier.Length == 0 || !identifier.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                return false;
            }
            if (!allowLeadingZeros && identifier.Length > 1 && identifier[0] == '0' && identifier.All(char.IsAsciiDigit))
            {
                return false;
            }
        }
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;

        if (Prerelease.Length == 0 && other.Prerelease.Length == 0) return 0;
        if (Prerelease.Length == 0) return 1;
        if (other.Prerelease.Length == 0) return -1;

        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var leftNumeric = long.TryParse(left[i], NumberStyles.None, CultureInfo.InvariantCulture, out var l);
            var rightNumeric = long.TryParse(right[i], NumberStyles.None, CultureInfo.InvariantCulture, out var r);
            int cmp;
            if (leftNumeric && rightNumeric) cmp = l.CompareTo(r);
            else if (leftNumeric) cmp = -1;
            else if (rightNumeric) cmp = 1;
            else cmp = string.CompareOrdinal(left[i], right[i]);
            if (cmp != 0) return cmp;
        }
        return left.Length.CompareTo(right.Length);
    }

    public bool Equals(SemanticVersion other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    public override string ToString()
    {
        var core = string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
        if (Prerelease.Length > 0) core += "-" + Prerelease;
        if (Build.Length > 0) core += "+" + Build;
        return core;
    }

    public static bool operator ==(SemanticVersion left, SemanticVersion right) => left.Equals(right);
    public static bool operator !=(SemanticVersion left, SemanticVersion right) => !left.Equals(right);
    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;
}
