using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Sanova.Core.Localization;

namespace Sanova.Core.Tests;

public class LocalizationTests
{
    private static LanguagePack Pack(string json) => LanguagePack.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), "test");

    private const string English = """
        {
          "_meta": { "code": "en", "name": "English", "nativeName": "English", "culture": "en-US" },
          "home": { "play": "PLAY", "only": "English only" },
          "files": { "one": "{count} file", "other": "{count} files" },
          "greet": "Hello {name}, {{literal}}",
          "format": { "bytes": "{value} {unit}" }
        }
        """;

    private const string Turkish = """
        {
          "_meta": { "code": "tr", "name": "Turkish", "nativeName": "Türkçe", "culture": "tr-TR" },
          "home": { "play": "OYNA" },
          "files": { "one": "{count} dosya", "other": "{count} dosya" },
          "greet": "Merhaba {name}",
          "format": { "bytes": "{value} {unit}" }
        }
        """;

    private static Localizer CreateLocalizer() => new([Pack(English), Pack(Turkish)]);

    [Fact]
    public void Flattens_nested_keys_and_switches_language()
    {
        var localizer = CreateLocalizer();
        Assert.Equal("PLAY", localizer["home.play"]);
        var raised = 0;
        localizer.LanguageChanged += (_, _) => raised++;
        localizer.SetLanguage("tr");
        Assert.Equal("OYNA", localizer["home.play"]);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Falls_back_to_english_then_to_the_key()
    {
        var localizer = CreateLocalizer();
        localizer.SetLanguage("tr");
        Assert.Equal("English only", localizer["home.only"]);
        Assert.Equal("missing.key", localizer["missing.key"]);
        Assert.Equal("en", localizer.SetLanguage("xx"));
    }

    [Fact]
    public void Formats_named_placeholders_and_escapes()
    {
        var localizer = CreateLocalizer();
        Assert.Equal("Hello Carl, {literal}", localizer.Get("greet", ("name", "Carl")));
        Assert.Equal("Hello {name}, {literal}", localizer.Get("greet"));
    }

    [Fact]
    public void Selects_plural_forms_per_language()
    {
        var localizer = CreateLocalizer();
        Assert.Equal("1 file", localizer.Plural("files", 1));
        Assert.Equal("3 files", localizer.Plural("files", 3));
        localizer.SetLanguage("tr");
        Assert.Equal("3 dosya", localizer.Plural("files", 3));
    }

    [Theory]
    [InlineData("ru", 1, PluralCategory.One)]
    [InlineData("ru", 3, PluralCategory.Few)]
    [InlineData("ru", 11, PluralCategory.Many)]
    [InlineData("ru", 22, PluralCategory.Few)]
    [InlineData("pl", 5, PluralCategory.Many)]
    [InlineData("fr", 0, PluralCategory.One)]
    [InlineData("tr", 1, PluralCategory.One)]
    [InlineData("ar", 2, PluralCategory.Two)]
    public void Plural_rules_follow_cldr(string language, long count, PluralCategory expected)
        => Assert.Equal(expected, PluralRules.Select(language, count));

    [Fact]
    public void Numbers_use_the_selected_language_culture()
    {
        var localizer = CreateLocalizer();
        localizer.SetLanguage("tr");
        Assert.Equal("1,5 KB", localizer.FormatBytes(1536));
        localizer.SetLanguage("en");
        Assert.Equal("1.5 KB", localizer.FormatBytes(1536));
    }

    [Fact]
    public void Resolves_system_language()
    {
        var localizer = CreateLocalizer();
        Assert.Equal("tr", localizer.ResolveSystemLanguage(CultureInfo.GetCultureInfo("tr-TR")));
        Assert.Equal("en", localizer.ResolveSystemLanguage(CultureInfo.GetCultureInfo("de-DE")));
    }

    [Fact]
    public void Rejects_files_without_metadata()
        => Assert.Throws<InvalidDataException>(() => Pack("""{ "home": { "play": "PLAY" } }"""));
}

/// <summary>Guards the shipped translations: every language must cover every English key with the same placeholders.</summary>
public partial class ShippedLanguageFileTests
{
    [GeneratedRegex(@"\{([A-Za-z]+)(?::[^}]*)?\}")]
    private static partial Regex Placeholder();

    private static string LocalizationFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SANOVA.Launcher.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "Sanova.Launcher", "Localization");
    }

    private static Dictionary<string, LanguagePack> LoadAll()
        => Directory.EnumerateFiles(LocalizationFolder(), "*.json")
            .Select(file => { using var stream = File.OpenRead(file); return LanguagePack.Load(stream, Path.GetFileName(file)); })
            .ToDictionary(p => p.Info.Code);

    [Fact]
    public void English_and_turkish_are_shipped()
    {
        var packs = LoadAll();
        Assert.Contains("en", packs.Keys);
        Assert.Contains("tr", packs.Keys);
        Assert.Equal("Türkçe", packs["tr"].Info.NativeName);
    }

    [Fact]
    public void Every_language_has_exactly_the_english_keys()
    {
        var packs = LoadAll();
        var english = packs["en"].Strings.Keys.ToHashSet();
        foreach (var (code, pack) in packs.Where(p => p.Key != "en"))
        {
            var keys = pack.Strings.Keys.ToHashSet();
            var missing = english.Except(keys).Order().ToList();
            var extra = keys.Except(english).Order().ToList();
            Assert.True(missing.Count == 0, $"{code} is missing: {string.Join(", ", missing)}");
            Assert.True(extra.Count == 0, $"{code} has unknown keys: {string.Join(", ", extra)}");
        }
    }

    [Fact]
    public void Placeholders_match_english()
    {
        var packs = LoadAll();
        var english = packs["en"];
        foreach (var (code, pack) in packs.Where(p => p.Key != "en"))
        {
            foreach (var (key, text) in pack.Strings)
            {
                var expected = Placeholder().Matches(english.Strings[key]).Select(m => m.Groups[1].Value).ToHashSet();
                var actual = Placeholder().Matches(text).Select(m => m.Groups[1].Value).ToHashSet();
                // Plural forms may legitimately omit {count} (e.g. "yesterday"), never add unknown names.
                Assert.True(actual.IsSubsetOf(expected.Append("count").ToHashSet()), $"{code}:{key} uses unknown placeholders {string.Join(",", actual.Except(expected))}");
                if (!key.EndsWith(".one", StringComparison.Ordinal) && !key.EndsWith(".other", StringComparison.Ordinal))
                {
                    Assert.True(expected.SetEquals(actual), $"{code}:{key} placeholders differ: expected {string.Join(",", expected)}, got {string.Join(",", actual)}");
                }
            }
        }
    }

    [Fact]
    public void No_translation_is_empty()
    {
        foreach (var (code, pack) in LoadAll())
        {
            var empty = pack.Strings.Where(s => string.IsNullOrWhiteSpace(s.Value)).Select(s => s.Key).ToList();
            Assert.True(empty.Count == 0, $"{code} has empty values: {string.Join(", ", empty)}");
        }
    }

    [Fact]
    public void Every_error_code_has_a_message()
    {
        var english = LoadAll()["en"].Strings;
        var codes = typeof(ErrorCodes).GetFields().Select(f => (string)f.GetValue(null)!);
        foreach (var code in codes)
        {
            Assert.True(english.ContainsKey($"error.{code}"), $"error.{code} has no message");
        }
    }

    [Fact]
    public void Plural_keys_define_one_and_other()
    {
        foreach (var (code, pack) in LoadAll())
        {
            foreach (var key in pack.Strings.Keys.Where(k => k.EndsWith(".one", StringComparison.Ordinal)))
            {
                Assert.True(pack.Strings.ContainsKey(key[..^4] + ".other"), $"{code}:{key} has no .other form");
            }
        }
    }
}
