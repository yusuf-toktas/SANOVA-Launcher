namespace Sanova.Core.Localization;

/// <summary>CLDR plural categories. Translation keys use them as suffixes, e.g. <c>files.one</c>.</summary>
public enum PluralCategory
{
    Zero,
    One,
    Two,
    Few,
    Many,
    Other,
}

/// <summary>
/// Cardinal plural rules (integer subset of CLDR) for the languages the launcher ships
/// and the ones contributors are most likely to add next. Unknown languages use one/other.
/// </summary>
public static class PluralRules
{
    public static PluralCategory Select(string languageCode, long count)
    {
        var n = Math.Abs(count);
        var mod10 = n % 10;
        var mod100 = n % 100;

        switch (languageCode)
        {
            // Languages without grammatical plural for counted nouns.
            case "ja" or "ko" or "zh" or "vi" or "th" or "id" or "ms":
                return PluralCategory.Other;

            // 0 and 1 are singular.
            case "fr" or "pt-br":
                return n is 0 or 1 ? PluralCategory.One : PluralCategory.Other;

            case "ru" or "uk" or "be":
                if (mod10 == 1 && mod100 != 11) return PluralCategory.One;
                if (mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14) return PluralCategory.Few;
                return PluralCategory.Many;

            case "pl":
                if (n == 1) return PluralCategory.One;
                if (mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14) return PluralCategory.Few;
                return PluralCategory.Many;

            case "cs" or "sk":
                if (n == 1) return PluralCategory.One;
                if (n is >= 2 and <= 4) return PluralCategory.Few;
                return PluralCategory.Other;

            case "ar":
                if (n == 0) return PluralCategory.Zero;
                if (n == 1) return PluralCategory.One;
                if (n == 2) return PluralCategory.Two;
                if (mod100 is >= 3 and <= 10) return PluralCategory.Few;
                if (mod100 is >= 11 and <= 99) return PluralCategory.Many;
                return PluralCategory.Other;

            // English, Turkish, German, Spanish, Italian, Dutch, Swedish, ... : one/other.
            default:
                return n == 1 ? PluralCategory.One : PluralCategory.Other;
        }
    }

    public static string Suffix(PluralCategory category) => category switch
    {
        PluralCategory.Zero => "zero",
        PluralCategory.One => "one",
        PluralCategory.Two => "two",
        PluralCategory.Few => "few",
        PluralCategory.Many => "many",
        _ => "other",
    };
}
