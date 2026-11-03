using System.Globalization;
using Sanova.Core.Diagnostics;
using Sanova.Core.IO;

namespace Sanova.Core.Tests;

public class SafePathTests
{
    [Theory]
    [InlineData("gta_sa.exe", "gta_sa.exe")]
    [InlineData("modloader\\HD Roads\\roads.txd", "modloader/HD Roads/roads.txd")]
    [InlineData("data/maps/LA/LAe.ipl", "data/maps/LA/LAe.ipl")]
    [InlineData("Türkçe klasör/dosya.txt", "Türkçe klasör/dosya.txt")]
    public void Accepts_and_normalizes_ordinary_paths(string input, string expected)
        => Assert.Equal(expected, SafePath.NormalizeRelative(input));

    [Theory]
    [InlineData("")]
    [InlineData("../evil.dll")]
    [InlineData("models/../../evil.dll")]
    [InlineData("./gta_sa.exe")]
    [InlineData("/absolute.txt")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("C:\\Windows\\system32\\x.dll")]
    [InlineData("C:relative.txt")]
    [InlineData("file.txt:stream")]
    [InlineData("CON")]
    [InlineData("data/NUL.txt")]
    [InlineData("com1.ini")]
    [InlineData("LPT9")]
    [InlineData("folder/trailingdot.")]
    [InlineData("folder/trailing space ")]
    [InlineData("a//b")]
    [InlineData("a/b/")]
    [InlineData("MODLOA~1/x.asi")]
    [InlineData("bad<name>.txt")]
    [InlineData("tab\there")]
    public void Rejects_unsafe_paths(string input)
    {
        Assert.False(SafePath.TryNormalizeRelative(input, out _, out var reason));
        Assert.NotNull(reason);
        var ex = Assert.Throws<SanovaException>(() => SafePath.NormalizeRelative(input));
        Assert.Equal(ErrorCodes.PathUnsafe, ex.Code);
    }

    [Fact]
    public void Rejects_overlong_paths() => Assert.False(SafePath.TryNormalizeRelative(new string('a', 300), out _, out _));

    [Fact]
    public void Resolved_paths_never_escape_the_root()
    {
        using var temp = new TempDir();
        var full = SafePath.GetFullPathUnder(temp.Path, "data/gta.dat");
        Assert.StartsWith(temp.Path, full, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<SanovaException>(() => SafePath.GetFullPathUnder(temp.Path, "../outside.txt"));
    }

    [Fact]
    public void Containment_checks_respect_segment_boundaries()
    {
        Assert.True(SafePath.IsStrictlyUnder(@"C:\Games\SANOVA", @"C:\Games\SANOVA\gta_sa.exe"));
        Assert.False(SafePath.IsStrictlyUnder(@"C:\Games\SANOVA", @"C:\Games\SANOVA2\gta_sa.exe"));
        Assert.False(SafePath.IsStrictlyUnder(@"C:\Games\SANOVA", @"C:\Games\SANOVA"));
        Assert.True(SafePath.Overlaps(@"C:\Games\GTA San Andreas", @"C:\Games\GTA San Andreas\SANOVA"));
        Assert.False(SafePath.Overlaps(@"C:\Games\GTA San Andreas", @"C:\Games\SANOVA"));
    }

    [Fact]
    public void Comparisons_are_culture_independent_under_turkish_culture()
    {
        // The Turkish dotted/dotless i breaks culture-sensitive comparisons: "FILE.INI" vs "file.ini".
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.True(SafePath.Comparer.Equals("MODLOADER/FILE.INI", "modloader/file.ini"));
            Assert.True(SafePath.IsStrictlyUnder(@"C:\GAMES\SANOVA", @"c:\games\sanova\GTA_SA.EXE"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}

public class SemanticVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "")]
    [InlineData("v0.4.0", 0, 4, 0, "")]
    [InlineData("V2.0", 2, 0, 0, "")]
    [InlineData("1.0.0-beta.2+build.7", 1, 0, 0, "beta.2")]
    public void Parses(string text, int major, int minor, int patch, string prerelease)
    {
        var version = SemanticVersion.Parse(text);
        Assert.Equal((major, minor, patch, prerelease), (version.Major, version.Minor, version.Patch, version.Prerelease));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-01")]
    public void Rejects_invalid(string text) => Assert.False(SemanticVersion.TryParse(text, out _));

    [Fact]
    public void Orders_by_semver_precedence()
    {
        string[] ordered = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0"];
        var parsed = ordered.Select(SemanticVersion.Parse).ToList();
        for (var i = 1; i < parsed.Count; i++) Assert.True(parsed[i - 1] < parsed[i], $"{ordered[i - 1]} < {ordered[i]}");
        Assert.Equal(SemanticVersion.Parse("1.0.0+a"), SemanticVersion.Parse("1.0.0+b"));
    }
}

public class AtomicFileTests
{
    [Fact]
    public void Replaces_content_without_leaving_temporary_files()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "state.json");
        AtomicFile.WriteAllText(path, "one");
        AtomicFile.WriteAllText(path, "two");
        Assert.Equal("two", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(temp.Path));
    }
}

public class RedactorTests
{
    [Fact]
    public void Removes_profile_path_user_and_computer_names()
    {
        var redactor = new Redactor(@"C:\Users\Deniz", "Deniz", "GAMING-PC");
        var text = redactor.Redact(@"Log at C:\Users\Deniz\AppData\Local\SANOVA by Deniz on GAMING-PC; c:/users/deniz/docs; DenizX stays");
        Assert.DoesNotContain(@"C:\Users\Deniz", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%\\AppData", text);
        Assert.Contains("by <user> on <computer>", text);
        Assert.Contains("DenizX stays", text);
    }
}

public class PathEncodingTests
{
    [Fact]
    public void Plain_ascii_paths_are_always_compatible() => Assert.True(PathEncoding.IsAnsiCompatible(@"C:\Games\SANOVA"));

    [Fact]
    public void Characters_outside_every_western_code_page_are_rejected()
        => Assert.False(PathEncoding.IsAnsiCompatible(@"C:\ゲーム\SANOVA"));
}
