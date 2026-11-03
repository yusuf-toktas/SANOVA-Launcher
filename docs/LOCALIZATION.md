# Localization

The launcher ships in **English** (`en`, the fallback) and **Turkish** (`tr`). On first start it
follows the Windows display language when a matching language exists, otherwise English. Players
switch languages under **Settings → Language**; navigation, page text, release notes and status
messages change immediately. Dialogs and notifications retain the language used when shown.

Interface strings live in language files. Content names and release notes come from the manifest;
technical diagnostic details and upstream error messages may remain in English.

## Language files

Built-in languages are JSON files in [`src/Sanova.Launcher/Localization/`](../src/Sanova.Launcher/Localization),
embedded into the executable at build time:

```jsonc
{
  "_meta": {
    "code": "tr",                // language code: "tr", "de", "pt-br" …
    "name": "Turkish",           // English name
    "nativeName": "Türkçe",      // shown in the language picker
    "culture": "tr-TR"           // .NET culture for numbers, dates and sizes
  },
  "nav": {
    "home": "Ana sayfa"
  },
  "home": {
    "status": {
      "play": { "title": "Oynamaya hazır" }
    }
  }
}
```

Nesting is for organization: `home.status.play.title` is the key of the last string above.
Group keys by screen (`home`, `setup`, `mods`, `settings` …) and keep shared words under
`common`. English (`en.json`) is the reference: it defines which keys exist.

### Placeholders

Values are inserted with named placeholders: `"Launcher {version}"`. A format can follow a colon,
using .NET format strings in the language's culture: `{percent:N0}`, `{size:N1}`. Write `{{` and
`}}` for literal braces. Placeholders must keep their names in every translation, but may move
anywhere in the sentence:

```json
"en": "{count} files from {source}",
"tr": "{source} kaynağından {count} dosya"
```

Sizes, speeds, durations, dates and relative times ("2 minutes ago") are formatted by the
launcher in the current language, so translations receive ready-made text such as `1,5 GB`.

### Plurals

Counted text is an object with [CLDR plural categories](https://cldr.unicode.org/index/cldr-spec/plural-rules)
as keys, and the number is available as `{count}`:

```json
"warningCount": { "one": "{count} warning", "other": "{count} warnings" }
```

English and Turkish use `one` and `other`. Turkish often uses the same form for both (`"{count}
uyarı"`), but both keys must be present. Languages with more forms use them: Russian and Polish
need `one`, `few` and `many`; Arabic `zero`, `one`, `two`, `few`, `many` and `other`. The rules
are in [`PluralRules.cs`](../src/Sanova.Core/Localization/PluralRules.cs); languages not listed
there use `one`/`other`. A missing category falls back to `other`.

### Fallback

A key missing from the current language uses the English text (and is logged once as a
warning), so an incomplete translation never shows raw keys. Text from content manifests
(component names and descriptions, release notes) follows the same rule: the player's language,
then English, then whatever is available.

## Adding a language

1. Copy `en.json` to `<code>.json` in `src/Sanova.Launcher/Localization/` and fill in `_meta`.
2. Translate the values. Keep keys, nesting and placeholder names unchanged.
3. If the language needs plural categories other than `one`/`other` and isn't in
   `PluralRules.cs`, add its rule there (and a test in `LocalizationTests.cs`).
4. Build and run the tests. `ShippedLanguageFileTests` fails if any language is missing an English
   key, has keys English doesn't have, or uses different placeholders — the failure lists them.
5. Run the launcher, pick the language in Settings, and check every page for clipped or
   overflowing text; translations are often noticeably longer than English.

No code changes are needed: the language picker lists every embedded file.

### Trying a translation without building

Translators can test a file with an installed launcher: put `<code>.json` in
`%LocalAppData%\SANOVA\Languages\` and restart the launcher. A drop-in file with the same code as
a built-in language replaces it, which is handy for reviewing fixes. Invalid files are skipped and
the reason is written to the log (Diagnostics → Recent log).

## Using strings in code

XAML uses the `Tr` markup extension, which updates when the language changes:

```xml
<TextBlock Text="{l:Tr settings.language.title}" />
```

View models expose a `LocalizedString` for text that depends on state. It re-evaluates its
function whenever the language changes, so composed messages also update instantly:

```csharp
StatusTitle = new LocalizedString(l => _state.IsInstalled ? l.T("home.status.play.title") : l.T("home.status.install.title"));
StatusTitle.Refresh(); // when the state it reads changes
```

Rules:

- Never put user-facing text in C# or XAML; add a key to `en.json` and `tr.json` together.
- Don't build sentences from fragments (`"Downloaded " + n + " files"`): word order differs
  between languages. Use one key with placeholders.
- Use plurals for every count shown to players.
- Errors from the engine carry a stable code (`SanovaException.Code`); the UI shows
  `error.<code>` with the exception's arguments as placeholders. Add both keys when adding an error
  code.
- Log messages and diagnostic reports stay in English, so the team can read reports from any
  player.

## Turkish specifics

- Case conversion: never call `ToUpper()`/`ToLower()` on UI text without the right culture —
  `i` becomes `İ` in Turkish. The launcher doesn't transform text case; write strings in the case
  they should appear.
- Comparisons of paths, keys and identifiers use ordinal, culture-independent comparison
  (analyzer rules CA1307, CA1310 and CA1311 report culture-sensitive calls as warnings, which
  fail CI builds), so a Turkish Windows installation behaves exactly like an English one.
- Number formatting follows the culture: `1.234,5 MB` in Turkish, `1,234.5 MB` in English.
