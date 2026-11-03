# Testing

```powershell
dotnet test SANOVA.Launcher.slnx                                    # everything (about a minute)
dotnet test tests/Sanova.Core.Tests                                 # engine only
dotnet test tests/Sanova.Core.Tests --filter "FullyQualifiedName~TransactionEngineTests"
```

Tests use [xUnit v3](https://xunit.net/). They never touch the real game, the player's documents
or `%LocalAppData%\SANOVA`: every file operation runs in a temporary folder that is deleted
afterwards, network access goes through an in-memory HTTP server, and registry lookups use a fake
registry.

## Strategy

The risky parts of a launcher are the ones that change files on a player's computer, so most
tests exercise the **real engine end to end** rather than mocks:

1. Build a real content release with the real maintainer tool (`ContentFixture` in
   `InstallationTests.cs`) from a temporary `Root/` folder.
2. Create a fake game folder with the files the compatibility checker expects.
3. Install, update, verify, repair, change components and uninstall with the real
   `InstallationService`, then assert on the resulting files and the recorded state.

Failure paths get as much attention as success paths:

| Area | What is proven |
|---|---|
| Transactions (`TransactionTests.cs`) | Every operation kind commits; a failure midway restores the exact previous state; cancellation rolls back; a simulated crash during the apply phase is rolled back on the next start; a crash after the commit point is completed. |
| Installation (`InstallationTests.cs`) | The original game is never modified; verification finds damage and repair fixes it; updates apply only differences and keep player files; disabling parks files and re-enabling needs no download; removing content restores the original file it replaced; a corrupted package aborts before anything is applied; uninstall keeps player files; install-path problems are explained. |
| Downloads (`DownloadTests.cs`) | Verified downloads; resuming after a dropped connection; servers that ignore ranges; corrupted content is rejected and leaves nothing behind; mirrors and retries; reading one zip entry over HTTP ranges without downloading the archive; the speed limit. |
| Release tool (`InstallationTests.cs`, `ManifestBuilderTests`) | Unassigned files and unknown redistribution are refused; original game files are never packaged; external components reference the author's archive; unchanged packages are reused; builds are reproducible; the documented example config stays valid. |
| Manifests and components (`ContentTests.cs`) | Unsafe paths, duplicates, collisions, cycles and non-HTTPS external sources are rejected; newer schemas need a newer launcher; signatures verify, tampering is detected and the trust policy is enforced; dependency and conflict cascades. |
| Game (`GameTests.cs`) | Registry, Steam library and SA-MP discovery; executable identification by hash and classification of unrecognized executables; manifest compatibility rules; missing and modified files and leftover mods. When `SANOVA_REAL_GAME_PATH` points to an original v1.0 installation, two read-only tests also check it. |
| Updates (`UpdateSourceTests.cs`) | Newest release per channel (pre-releases only on beta, drafts ignored); conditional requests with ETags; clear errors for rate limits and missing repositories; unconfigured sources; local releases verified, cached and reloaded; offline checks report "offline" and keep the last verified manifest; developer overrides only in developer mode. |
| Foundations (`FoundationTests.cs`, `SettingsAndSavesTests.cs`) | Path safety (traversal, rooted paths, device names, alternate streams, 8.3 short names, overlong paths), culture-independent comparisons under a Turkish culture, version ordering, atomic writes, log redaction, ANSI path check, settings corruption recovery, save backup and restore. |
| Localization (`LocalizationTests.cs`) | Placeholders, plurals, fallback, system language detection; every shipped language has exactly the English keys with the same placeholders. |

## UI tests

`tests/Sanova.Launcher.Tests` runs the real application — the same `App`, views, view models
and services — on Avalonia's headless platform with Skia rendering. `UiScenario` builds
complete situations (first run, nothing published yet, ready to install, installed, update
available) using a real temporary content release, then drives the UI:

- the main button follows the installation state (Install → Update → Play …);
- switching language updates visible text immediately;
- switching theme changes the surface colours;
- enabling a mod pulls in its dependency and shows the pending change.

`SnapshotTests` render each main screen to PNG in
`tests/Sanova.Launcher.Tests/snapshots/` (ignored by git) for visual review. They aren't
compared pixel by pixel; open them after changing styles or layouts.

The headless renderer draws some Turkish combining marks differently (for example the dots on
`İ` and `Ö` may be missing in snapshots). The real application renders them correctly; check
Turkish text in the running launcher, not in snapshots.

## Opt-in tests with the real game

A few tests use a real, unmodified GTA San Andreas v1.0 installation. They only read from it, and
are skipped unless `SANOVA_REAL_GAME_PATH` points to the game folder:

- two quick checks: the executable is identified as v1.0, and the installation passes the
  compatibility check against the shipped reference list;
- with `SANOVA_REAL_GAME_TEST=1` as well, `RealGameIntegrationTests` copies the whole game
  (about 5 GB) into the temp folder, applies a small release, checks every original file against
  the reference hashes, runs a full verification, uninstalls, and confirms the original
  installation is unchanged.

```powershell
$env:SANOVA_REAL_GAME_PATH = 'D:\Games\GTA San Andreas'   # your original v1.0 folder
$env:SANOVA_REAL_GAME_TEST = '1'                          # also run the full install test
dotnet test tests/Sanova.Core.Tests --filter "FullyQualifiedName~RealGame"
```

Run them before releases that change copying, verification or installation layout.

## Manual checks before a release

Automated tests can't cover everything a player sees. Before a release, on a clean Windows user
account or VM:

- Install with `Setup.exe` as a standard (non-administrator) user.
- First-run setup with the game detected automatically, and with a manually chosen folder.
- Install, play, close the game; update from the previous content release; disable and enable a
  mod; verify; repair after deleting a SANOVA file; uninstall.
- Disconnect the network: the launcher starts, says it's offline, and still plays.
- Close the launcher during a download, start it again, and resume.
- Switch language and theme; check every page in Turkish for clipped text.
- Launcher self-update from the previous version ([Releasing](RELEASING.md)).
- Create a diagnostic report and check it contains no user name, computer name or profile path.

## Continuous integration

[`.github/workflows/ci.yml`](../.github/workflows/ci.yml) builds the solution in Release with
warnings treated as errors and runs all tests on Windows for every push and pull request. Test
results and UI snapshots are kept as workflow artifacts.

## Writing new tests

- Use `TempDir` for files and `FakeHttpServer` for HTTP (`TestSupport.cs`); never depend on the
  network or on files outside the temp folder.
- For engine behavior, prefer a `ContentFixture` scenario over mocks.
- For a bug fix, first add a test that fails without the fix.
- UI tests use `[AvaloniaFact]` and `UiScenario`; keep page transitions disabled (the scenario
  does this) so assertions don't race animations.
