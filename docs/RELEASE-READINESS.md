# v0.1.0 release candidate readiness

This audit prepares the launcher for a release candidate. It does not publish v0.1.0 or
SANOVA Foundation. The parent game/mod directories are outside the maintenance scope;
installation tests use disposable fixtures.

## Verified repository configuration

- Public repository: `yusuf-toktas/SANOVA-Launcher`, default branch `main`.
- Active `Protect main` ruleset blocks deletion and non-fast-forward updates, without bypass
  actors. Ordinary maintainer pushes remain allowed. Required reviews/status checks are not
  imposed on this solo-maintainer workflow; CI still runs on every push and pull request.
- Actions defaults to read-only tokens, cannot approve pull requests, and requires actions
  pinned to full commit SHAs. Only the explicit draft-release job receives `contents: write`.
- Secret scanning, secret push protection and private vulnerability reporting are enabled.
  Dependency alerts and automatic security updates are enabled. Actions dependency updates
  are configured monthly.
- Bug/enhancement labels exist; issue forms have required fields and parse as YAML. The
  public issue chooser responds, but signed-in submission is a manual check.
- Contributor Covenant 2.1 is included. No private conduct contact has been invented; the
  code explains the available public/report-content routes and their privacy limits.

## Verification and limits

The Windows release audit on 2026-11-03 completed these local checks:

| Check | Result |
|---|---|
| Restore and Release build, `CI=true`, warnings as errors | Passed; zero warnings/errors. |
| Complete automated suite | 169 passed (153 engine, 16 UI), three opt-in real-game cases skipped, zero failed. |
| Localization and UI fixtures | English/Turkish key and placeholder parity, state transitions and language refresh passed; dark/light snapshots reviewed at 1200×780. |
| Static analysis | `dotnet format analyzers --verify-no-changes --severity warn` passed; NuGet audit found no known vulnerable direct/transitive packages. |
| Workflow validation | actionlint 1.7.12 passed; six GitHub YAML files and issue form structure parsed successfully. |
| Packaged maintainer tool | Help, disposable key generation, signed build/verify, tampered-manifest rejection and corrupt-package rejection passed. Test keys deleted. |
| Packaging guards | PowerShell parse and invalid version/output/junction rejection checks passed. |
| Final Windows x64 packaging | Self-contained setup, portable ZIP, full package, feeds and maintainer tool generated under `artifacts/rc-validation`; runtime and license files inspected. Unsigned, not installed. |

See [Testing](TESTING.md) for the automated coverage and the original test-count discrepancy,
and [Releasing](RELEASING.md) for packaging and the clean-Windows checklist. Local test
results, packages and tooling stay under ignored `artifacts/`; they are not official releases.

The security review covers manifest signatures and cache reloads, bounded manifest fetching,
SHA-256 verification, archive entry mapping, path containment, transaction recovery,
file ownership, settings, and developer/production source switching. Regression tests cover
the discovered failures. Existing junctions/reparse points are rejected; these managed path
checks are not a handle-based sandbox against a hostile local process racing filesystem
changes. Keep installation metadata and trusted keys under the user's normal filesystem
permissions. Public keys are public; private keys must remain outside repositories and logs.

Launcher self-update currently relies on the configured GitHub/HTTPS source and Velopack
package integrity. No Authenticode certificate or independent launcher-signing identity is
configured; do not describe the local installer as signed or clean-machine verified.

## Gates before an official release

| State | Remaining work |
|---|---|
| Manual verification | Clean Windows standard-user setup, upgrade/restart recovery and uninstall; 100%/150%/200% DPI; live screen-reader/keyboard checks; both languages/themes. |
| External resource | Configure an Authenticode identity if signed installers are required and assess SmartScreen on the signed build. |
| External resource | Select a real content source and generate/store the production signing key securely; distribute only its public key. Production content remains unconfigured and signature enforcement stays enabled. |
| Deferred to Foundation | Confirm components, versions, compatibility and redistribution permissions; prepare signed packages and run install/update/repair/removal against that actual content. |
| Publication withheld | Review the changelog, final packages and these gates before explicitly authorizing a tag, draft or public release. No release/tag is required for package-only workflow validation. |

The launcher repository is configured as its own update source. The first published launcher
release and a previous installed version do not yet exist, so a real GitHub self-update and
delta upgrade remain unverified. A separate content repository is proposed in the
[content pipeline](CONTENT-PIPELINE.md), but has not been created.
