---
name: desktop
description: Build, verify, package, or install the Windows desktop app from WSL through the shared Windows build tools. Use when asked to compile, rebuild, try a change in the installed app, or produce a local package. Do not use for releases or Store publishing.
---

# Windows desktop development

Goal: run the requested local Windows build and report what it proved.

1. Read [docs/windows-development.md](../../../docs/windows-development.md); it lists this repo's commands and what is specific to it, and links to the shared Windows build tools for everything else.
2. Pick the root npm command for the request: `verify:desktop` to compile, `package:desktop:dev` for a signed dev package, `package:desktop` for the unsigned Store package, or `install:desktop` to build, update the installed copy in place and launch it. "Rebuild and try it" means `install:desktop`. A build-only request does not authorise installing.
3. Leave the platform at its default (the build host's architecture) unless another one is asked for. Use `npm run install:desktop -- --skip-build` only when asked to reuse the last package.
4. Let the tooling own staging, the dev version, signing, the in-place update and cleanup. Do not edit the tracked version, uninstall to get past an update error, or change the signer or publisher.
5. After an install, check the installed version, that `SignatureKind` is `Developer`, and that the running executable is inside the package's `InstallLocation`. A path under WindowsApps alone does not distinguish a Store install from a sideloaded one. Stop any debug copy before launching. A sandboxed shell can return an empty package list; check in the desktop user's context before saying a package is absent.

Report: the command run, the resulting version and output location, the build or install result, and which runtime checks were done or still need a person. A compile or package build does not show that the app runs correctly.
