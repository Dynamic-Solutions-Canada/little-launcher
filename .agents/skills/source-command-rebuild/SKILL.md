---
name: source-command-rebuild
description: Build, update in place, and launch Little Launcher as a Release MSIX from WSL for local testing after changes.
---

# Rebuild and launch Little Launcher

Read [.codex/docs/windows-development.md](../../../.codex/docs/windows-development.md)
and the shared helper guide it links before building.

Run `npm run install:desktop` from the authoritative WSL repository root. It
copies the working tree into disposable C: Windows TEMP, allocates a dev revision
there, signs with the shared local certificate, updates the matching installed
family in place, verifies its version, and launches the manifest app. The helper
cleans staging and returns logs/packages to `.artifacts/windows-build/`.

Do not edit the tracked version for a dev build, uninstall to bypass an error,
or replace the signer. Little Launcher uses the preserved Store-identity dev
certificate so existing settings and profiles remain in the same package family.
Use `-- --platform x64` only for an x64 target. Debug or unpackaged runtime testing
is available only when specifically requested; `npm run verify:desktop` is a
compile-only Debug check and does not install.

After installation, inspect the installed version and `SignatureKind`, and verify
the running app comes from its `InstallLocation`. A sandbox can return an empty
package list; check the permitted desktop user's context before claiming absence.
Report the version actually running and available runtime checks. Build or install
failure must not be reported as a successful deployment. Leave changes uncommitted.
