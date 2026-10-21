# Launcher reorder regressions

Run from the repository root with .NET 10:

```sh
dotnet run --project tests/LauncherItemReorder.Tests
```

The executable returns nonzero on any failure and prints each case. It compiles the actual LauncherItem model, production LauncherItemReorder policy and LauncherDropGeometry group-boundary classifier, with the same observable-model generator version as the app. It has no test framework dependency and does not read user settings, open windows or contact sync services.

On Linux without a host SDK, use the existing isolated SDK image:

```sh
docker run --rm --user "$(id -u):$(id -g)" -e DOTNET_CLI_HOME=/tmp/dotnet-home -e NUGET_PACKAGES=/tmp/nuget -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0.402 dotnet run --project tests/LauncherItemReorder.Tests
```

Coverage includes same/cross-column moves, children into/out of/between groups, whole-group identity, empty targets, invalid nesting, synthetic-wrapper behavior, stale source/target collections and item references, duplicate destination membership, cancellation before mutation, no-op drops, insertion bounds and flatten/save/reload behavior.

The group-boundary cases check exact before/inside/after thresholds, preserved interior space for short groups, out-of-bounds and invalid geometry, and the different identities/membership saved by appending inside versus placing a sibling after a group. These 37 policy checks passed locally on 2026-10-21. They do not verify the live XAML coordinate transform or nested event delivery.

The later faded-last-child variant adds end-marker containment checks across list, large-icon, small-icon and tiny child dimensions, plus invalid geometry rejection. All 39 policy cases pass. These numeric checks do not prove native scrim rendering or cleanup; the guarded native suite reviews those against the saved geometry and held input receipts.

The native Windows suite must separately exercise both hosts in list, large-icon and small-icon modes: hover header to child and back; move a group over another group's child body; verify no nesting; hold an insertion position without item movement; cancel with Escape; check a compact group preview with the right name/count; then close/reopen and verify the saved order. Check light, dark and high contrast. These policy tests do not establish native pointer routing, live drag cancellation, preview rendering, accessibility or visual fidelity.

The repeatable installed-app suite and consoleless executor are in [native-drag](../native-drag/README.md). Run them only on the explicitly designated disposable desktop; they temporarily replace settings with synthetic fixtures and restore the previous file.
