# Contributing

Thanks for helping with SANOVA Launcher. This page covers the conventions; the
[README](README.md) explains how to build and run, and [docs/](docs/) explains how things work.

## Setup

- Windows 10/11, the [.NET 10 SDK](https://dotnet.microsoft.com/download), and any editor with C#
  support (Visual Studio, Rider, or VS Code with C# Dev Kit).
- `dotnet build SANOVA.Launcher.slnx`, then `dotnet test SANOVA.Launcher.slnx`.
- The launcher keeps its data in `%LocalAppData%\SANOVA`. Delete that folder to see the first-run
  setup again.
- The .NET SDK and Avalonia's build tooling send anonymous build telemetry. Set
  `DOTNET_CLI_TELEMETRY_OPTOUT=1` and `AVALONIA_TELEMETRY_OPTOUT=1` to opt out.

Report security problems privately as described in [SECURITY.md](SECURITY.md), not in public
issues.

## Ground rules

- **Never modify the player's original game.** Everything the launcher changes happens inside
  the SANOVA folder, through the transaction engine.
- **Never delete or overwrite files the launcher didn't place.** Unknown files are moved aside and
  reported, never removed.
- **No fake progress or success.** Every completed operation must have been performed and verified.
- **No piracy-related functionality**: no copy-protection workarounds, no executable patching
  for DRM, no redistribution of the original game.
- **Respect mod licenses.** Third-party content is packaged only with a recorded license or
  permission ([content pipeline](docs/CONTENT-PIPELINE.md#redistribution-policy)).
- **No administrator rights** unless a feature truly can't work without them.
- **No secrets or personal data in logs, settings or reports.** Use `Redactor` for anything that
  may contain paths or user names.

## Code

- Engine logic goes in `Sanova.Core`, which has no UI dependencies. The Avalonia app only
  presents state and calls the engine.
- Expected failures throw `SanovaException` with a code from `ErrorCodes`; add `error.<code>` to
  every language file.
- Every user-facing string goes in the language files ([Localization](docs/LOCALIZATION.md)).
- Paths and identifiers use ordinal comparisons (`StringComparison.Ordinal` /
  `OrdinalIgnoreCase`, or `SafePath.Comparer` for paths). The analyzers flag culture-sensitive
  calls, because they break on Turkish systems.
- Nullable reference types are on. CI builds with warnings as errors, so keep the build
  warning-free.
- Formatting follows `.editorconfig` (`dotnet format` applies it).

## Tests

Add or update tests with every change ([Testing](docs/TESTING.md)). Anything that changes files on
disk needs an end-to-end test in a temporary folder, including the failure path. A bug fix starts
with a test that reproduces the bug.

## Commits and pull requests

- Branch from `main`; keep pull requests focused on one change.
- Use [Conventional Commits](https://www.conventionalcommits.org/) prefixes (`feat:`, `fix:`,
  `docs:`, `test:`, `refactor:`, `build:`, `ci:`, `chore:`) followed by a short imperative
  summary, e.g. `fix: restore originals when a component is removed`. Explain why in the body
  when it isn't obvious.
- Add an entry under **Unreleased** in [CHANGELOG.md](CHANGELOG.md) for changes players or
  maintainers will notice.
- Commits are authored by the people who make them.
- Never commit private keys, tokens, certificates or game files. `.gitignore` covers the usual
  cases (`*.pem` except public keys, `secrets/`, build outputs).

Line endings are normalized by `.gitattributes`: text files are stored with LF and checked out
natively; PowerShell and batch scripts always use CRLF.

## License

SANOVA Launcher is licensed under the [Apache License 2.0](LICENSE). Unless you state otherwise,
contributions you submit are licensed under the same terms (section 5 of the license). Don't
submit code or assets you don't have the right to license this way. Third-party dependencies need
a license compatible with Apache-2.0 and an entry in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
