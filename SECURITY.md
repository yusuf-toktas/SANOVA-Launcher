# Security policy

## Reporting a vulnerability

Please don't report security problems in public issues. Use GitHub's private vulnerability
reporting instead: open the repository's **Security** tab and choose **Report a vulnerability**.
Describe what is affected, how to reproduce it, and the impact you expect. The discussion stays
private until a fix is available.

## Supported versions

No release has been published yet. Once releases exist, security fixes go into the latest
launcher release, and installed launchers receive them through the built-in update.

## Scope

In scope: SANOVA Launcher and the `sanova-manifest` tool in this repository, for example:

- writing, moving or deleting files outside the SANOVA installation folder, or modifying the
  player's original game;
- bypassing manifest signature or file hash verification;
- tampering with content or launcher updates;
- personal data leaking into logs, settings or diagnostic reports;
- anything that requires or grants administrator rights unexpectedly.

Out of scope: the original game, third-party mods (report those to their authors), and
vulnerabilities in dependencies that don't affect the launcher (report those upstream).
