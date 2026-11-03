# Changelog

All notable changes to SANOVA Launcher are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/). Content releases have their own release notes.

## [Unreleased]

## [0.1.0-rc.1] - 2026-11-03

First public **Launcher preview**, for Windows x64. This is not a SANOVA modpack
or Foundation content release. Official SANOVA content is not yet available for
installation. The production content source and trust keys are intentionally
unconfigured; content signature enforcement remains enabled.

### Preview limitations

- The installer and portable application are unsigned. Windows SmartScreen may
  display an unknown-publisher warning. Compare downloaded files with the
  published SHA-256 checksums before deciding whether to run them.
- A clean-machine installation, live self-update, uninstall, screen-reader and
  multi-DPI validation have not been performed. Automated fixture tests do not
  establish compatibility with a real SANOVA content collection.
- No GTA San Andreas files, third-party mod archives or official SANOVA content
  are included. A legally owned original PC game will be required when content
  becomes available.
- The portable ZIP is self-contained; extract it before running
  `SanovaLauncher.exe`. The setup installer is also self-contained for Windows
  x64. No separately installed .NET runtime is required by these packages.
- Prerelease self-updates are disabled by default in the distribution settings.
  No previous public launcher exists for a real upgrade/delta test.

### Downloads

- `SANOVA.Launcher-win-Setup.exe`: per-user Windows installer.
- `SANOVA.Launcher-win-Portable.zip`: portable Windows x64 distribution.
- Full `.nupkg`, `releases.win.json`, `RELEASES` and `assets.win.json`: Velopack
  package/feed assets used by the update infrastructure.
- `SHA256SUMS.txt`: SHA-256 digests of the distributed package/feed assets.

### Fixed

- Preserve transaction recovery data when post-commit backup preservation fails, and reject
  missing staged sources before changes begin.
- Reject existing filesystem reparse points at installation boundaries and hash managed files
  before destructive changes, including edits that retain their original size and timestamp.
- Recheck cached manifest signatures using the current trust policy; separate offline caches
  by content source and channel, and discard stale checks when sources change.
- Enforce production manifest signing while retaining explicit developer fixtures; bound
  manifest downloads even when servers omit their content length.
- Refresh localized update notes and prevent keyboard interaction behind modal dialogs.
- Pin workflow actions, separate package validation from draft creation, and validate package
  versions and cleanup boundaries before publishing local artifacts.

### Added

- First-run setup: language, game detection (retail registry keys, installed programs, Steam
  libraries, SA-MP settings, common folders) or manual selection, compatibility check, install
  folder, components.
- Game identification by executable hash, with plain-language explanations for the Steam/Rockstar
  Games Launcher re-release, the Definitive Edition, modified and unknown executables, missing
  and changed files, and leftover mods. Content releases can update the compatibility rules.
- Separate SANOVA installation copied from the player's game; the original is never modified.
  Install folders the 2005 game can't open (characters outside the ANSI code page) are rejected.
- Transactional installation engine: journaled changes, rollback on failure or cancellation,
  recovery after a crash or power loss, protection of files the player added or changed.
- Verify and repair; optional components with dependencies and conflicts; enable and disable
  without downloading; safe uninstall.
- Content releases from GitHub Releases, an HTTPS URL or a local folder (developer mode), with
  signed manifests (ECDSA P-256), stable and beta channels, and an offline manifest cache.
- Resumable, verified downloads with mirrors, retries, a speed limit and parallel transfers;
  differential updates that fetch only changed files, reading single files from large packages
  through HTTP range requests.
- Components from mod authors' own sources (automatic download or guided manual download),
  verified by hash, for mods that may not be redistributed.
- Launcher self-updates through Velopack (GitHub Releases or a feed URL).
- Game launch, running-game detection, automatic save backups and restore.
- Downloads page with progress, speed, time remaining and history; diagnostics page with checks,
  recent log and a shareable report (user name, computer name and profile paths removed).
- English and Turkish, switchable instantly, with drop-in language files.
- Dark and light themes with an original visual identity.
- `sanova-manifest` maintainer tool: build, sign, verify, key generation, base-game reference
  lists, Root/development-install sync check, config scaffolding, package reuse between releases.
  Refuses components with unknown redistribution status and unmodified original game files.
- Documentation, tests, CI and release workflows.
- Apache License 2.0 for the launcher's source code, with third-party notices shipped in the
  application's `licenses` folder.
