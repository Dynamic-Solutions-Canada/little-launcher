# Launcher sync regressions

Run `dotnet run --project tests/LauncherSync.Tests` with .NET 10. This links the real Windows observable launcher/item/bookmark models, `LauncherPayload` and `PortableLauncherIcon`. Only native dispatch, window invalidation, logging and unrelated network/service boundaries are adapted. Temporary image files exercise actual capture, serialization, materialization and in-place merge across different device paths. It does not open windows, read personal settings, contact an account or qualify the full WinUI build.

Read [docs/sync.md](../../docs/sync.md) and [icons.md](../../docs/icons.md) before changing it.

The harness also checks whole-launcher Platform ownership and portable glyph preview retention. For synthetic cross-language fixtures, run with `-- --roundtrip input.json output.json` to deserialize, apply and serialize through production payload code. Native glyph generation is adapted in this portable harness; it does not qualify Windows GDI or WinUI.

The harness now also compiles production UserSettings/SyncProviders with native side-effect adapters, proving login startup defaults on and explicit false survives WhenWritingDefault JSON. The web-image update case uses genuinely different raster bytes and checks a new materialized path, the existing model instance and a return payload. It does not execute first-run OS registration.
