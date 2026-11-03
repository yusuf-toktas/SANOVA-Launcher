# Changelog

All notable changes to SANOVA Launcher are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/). Content releases have their own release notes.

## [Unreleased]

The first version of the launcher, planned as **0.1.0**. Nothing has been published yet.

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
