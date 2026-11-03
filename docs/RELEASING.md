# Releasing

SANOVA ships two independent things, each with its own version and release:

| | Launcher | Content |
|---|---|---|
| What | `SanovaLauncher.exe` (this repository) | The SANOVA files installed into the game |
| Version | `Version` in [`Directory.Build.props`](../Directory.Build.props) | `contentVersion` in the content config |
| Built with | `build/package.ps1` (Velopack) | `sanova-manifest build` ([content pipeline](CONTENT-PIPELINE.md)) |
| Published to | GitHub Releases of the launcher repository | GitHub Releases of the content repository (or any HTTPS host) |
| Players get it | Launcher self-update (Settings → Updates) | Content update (Home → Update) |

Pushing a version tag runs the release workflow and creates a **draft**. Manual workflow runs
build and retain packages only by default; draft creation requires the `create_draft` opt-in.
Publishing a draft remains a separate maintainer action.

## Versioning

Both use [Semantic Versioning](https://semver.org/):

- **Major**: breaking changes (launcher: a new manifest schema or installation layout that older
  versions can't read; content: changes that need a fresh installation or break saves).
- **Minor**: new features or content.
- **Patch**: fixes.
- Pre-releases (`1.3.0-beta.1`) are marked as GitHub pre-releases. Launcher packages currently
  use the `win` Velopack channel; `launcherUpdates.allowPrerelease` controls visibility.

When content needs a newer launcher, set `minLauncherVersion` in the content config. Older
launchers then ask the player to update the launcher first instead of failing midway. Release
the launcher before the content that needs it.

The launcher reads manifests up to schema version 1. A future schema change must ship in a
launcher release first, and stay unused until most players have updated.

## Changelog

[`CHANGELOG.md`](../CHANGELOG.md) follows [Keep a Changelog](https://keepachangelog.com/):
add entries under **Unreleased** as changes land, then rename that section to the version when
releasing. Content release notes are separate Markdown files referenced from the content config
(`releaseNotesFiles`), one per language; the launcher shows them before updating.

## Before the first public release

1. **License.** Done: the launcher's source code is licensed under the Apache License 2.0
   (`LICENSE`, `NOTICE`, `THIRD-PARTY-NOTICES.md`).
2. **Repositories.** The source code lives in
   [yusuf-toktas/SANOVA-Launcher](https://github.com/yusuf-toktas/SANOVA-Launcher), which can
   also host launcher releases. Keep content releases in a separate repository, so each release
   list holds one kind of release.
3. **Manifest signing key.** `sanova-manifest keygen --out <secure folder>`. Store the private
   key outside the repository (password manager or offline backup) — whoever has it can publish
   content that every launcher trusts.
4. **`src/Sanova.Launcher/distribution.json`.**
   - `launcherUpdates.repository`: `"yusuf-toktas/SANOVA-Launcher"` (configured).
   - `content.repository`: a real, maintainer-approved content repository (or `content.type:
     "url"` + a real `manifestUrl`); currently unset because Foundation content is not ready.
   - `trust.publicKeys`: the contents of `sanova-manifest-public-key.pem`.
   - `trust.requireSignature`: `true` (configured; keep enabled). An empty public key list
     intentionally blocks production content until the signing setup is complete.
   - `links`: website, source code, community and support pages (optional).
5. **Code signing (recommended).** Unsigned installers trigger Windows SmartScreen warnings.
   With an Authenticode certificate, pass `-SignParams` (signtool parameters) or
   `-AzureTrustedSignFile` to `build/package.ps1`; Velopack signs the launcher, its installer and
   updater.
6. **Content.** Fill in the real content configuration, including every component's license
   and redistribution status, and publish the first content release.

## Releasing the launcher

1. Update `CHANGELOG.md` and bump `Version` in `Directory.Build.props`. If .NET, SkiaSharp or
   another shipped dependency changed, update `THIRD-PARTY-NOTICES.md` and refresh the notice
   files in `src/Sanova.Launcher/licenses/` from the new packages.
2. Restore, build and test:

   ```powershell
   dotnet restore SANOVA.Launcher.slnx
   dotnet build SANOVA.Launcher.slnx -c Release --no-restore -warnaserror
   dotnet test SANOVA.Launcher.slnx -c Release --no-build
   ```
3. Get the previous release's files, so Velopack can build a delta package:

   ```powershell
   dotnet tool restore
   dotnet vpk download github --repoUrl https://github.com/<owner>/<launcher-repo> -o artifacts/releases
   ```

4. Build: `./build/package.ps1 -Version 1.2.0 -ReleaseNotes notes/launcher-1.2.0.md`.
   `artifacts/releases` then holds `SANOVA.Launcher-win-Setup.exe`, a portable zip, the full
   `.nupkg` package and `releases.win.json`. A delta package is generated only when a previous
   full package is available. `-IncludeTools` adds the self-contained manifest tool and licenses.
   Use a dedicated output directory; packaging rejects linked output paths and the repository
   root to protect files during cleanup. Windows x64 is the verified packaging target; ARM64
   is accepted by the script but needs independent runtime and installer validation.
5. Test on a clean Windows user account or VM: a fresh install with `Setup.exe`, and an update
   from the previous version (see below).
6. Upload as a draft, review, then publish on GitHub:

   ```powershell
   dotnet vpk upload github --repoUrl https://github.com/<owner>/<launcher-repo> --token <token> `
       -o artifacts/releases --tag v1.2.0 --releaseName "SANOVA Launcher 1.2.0"
   ```

   Add `--pre` for a beta. Velopack's update check only sees published releases (pre-releases
   only when `launcherUpdates.allowPrerelease` is on).

Alternatively, push a tag `v1.2.0`: the [release workflow](../.github/workflows/release.yml)
runs the tests, builds the packages (with a delta against the latest published release) and
creates a **draft** release for review in the repository it runs in (so that repository should
be the one named in `launcherUpdates.repository`). Release notes come from the version's section
in `CHANGELOG.md`. It refuses to build while `distribution.json` has no launcher update source,
so a launcher that can't update itself never ships by accident. It rejects disabled content
signature enforcement or configured production content without verification keys. An unconfigured
content source with no keys can be packaged, but cannot install production content. Build/test/package
jobs use read-only repository permissions; only the separate draft-upload job can write releases.

For a non-publishing GitHub validation, manually run **Release launcher**, supply the version,
and leave **Create a draft release** unchecked (`create_draft=false`). This builds/tests/packages
and uploads Actions artifacts without creating a tag or release. Do not select draft creation
until a release is intentionally authorized.

### Testing a launcher update locally

Without publishing anything, point a launcher at a local folder of Velopack releases:

```powershell
./build/package.ps1 -Version 0.1.0 -Output ./artifacts/update-test
Expand-Archive ./artifacts/update-test/releases/SANOVA.Launcher-win-Portable.zip ./artifacts/update-test/portable-0.1.0
./build/package.ps1 -Version 0.2.0 -Output ./artifacts/update-test   # same folder → delta package
$env:SANOVA_LAUNCHER_UPDATE_URL = (Resolve-Path ./artifacts/update-test/releases).Path
& './artifacts/update-test/portable-0.1.0/SanovaLauncher.exe'
```

Settings → Updates → **Check for updates** offers 0.2.0; **Download and restart** downloads
the delta, and **Restart to update** restarts into 0.2.0. The environment variable overrides
`distribution.json` for that process only.

Velopack applies a delta only when the previous full package is available locally. The portable
build doesn't keep one, so it downloads the full package instead ("no local/base package
available" in `%LocalAppData%\velopack\velopack_SANOVA.Launcher.log`). Check that log after
testing an update of a `Setup.exe` installation to confirm it received the delta. Run interactive
tests in a disposable Windows account or VM: a portable launcher still uses that account's
`%LocalAppData%\SANOVA` settings.

### Installer verification boundary

The release audit generated the Windows x64 setup, portable archive, full package, release feeds
and self-contained maintainer tool locally. Package inspection confirmed the .NET runtime,
native rendering dependencies and the `licenses/` texts. English/Turkish and distribution JSON
are embedded application resources. The app manifest requests `asInvoker` and PerMonitorV2 DPI;
no machine-wide MSI or prerequisite runtime download is requested by the packaging script.

No clean Windows install, actual self-update or uninstall has been verified. Before publishing,
use an ordinary, non-administrator account in a disposable VM to test:

- Setup into the default per-user location and first launch without a separately installed .NET SDK/runtime.
- Both languages, theme persistence, keyboard focus and 100%/150%/200% display scaling.
- Upgrade from a locally built earlier version using the local feed, including interrupted download/restart recovery.
- Uninstall through Windows Settings; confirm launcher binaries/shortcuts are removed and all
  original game, SANOVA content and unrelated user files remain intact. Check whether settings
  are retained and document the observed behavior.

The local audit packages are unsigned because no Authenticode identity is configured. A trusted
signing identity and SmartScreen behavior remain external release tasks.

## Releasing content

See the [content pipeline](CONTENT-PIPELINE.md): build and sign with `sanova-manifest build`,
verify with `sanova-manifest verify`, test through **Developer mode → Local folder**, then
create the GitHub release in the content repository and upload the files listed in
`release-assets.txt`. Publish beta content as a pre-release.

## Rolling back

- **Content:** publish a new release with a higher version that restores the previous files.
  Players' installations move forward to it like any update, and unchanged files aren't
  downloaded again. Avoid deleting a published release: later releases may reference its
  packages.
- **Launcher:** publish a new patch version built from the previous good commit. Velopack moves
  players forward to it; versions are never downgraded automatically.

## Repository hygiene

- Never commit private keys, tokens or certificates. `.gitignore` excludes `*.pem` except public
  keys, and `secrets/`.
- Release workflows use the repository's `GITHUB_TOKEN`; no personal tokens are stored in the
  repository.
- Commits and releases are authored by the maintainers who make them.
