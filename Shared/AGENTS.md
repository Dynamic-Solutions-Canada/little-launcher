# Shared launcher pin metadata

`PinSnapshot.cs` is compiled into the main app and Native AOT companion. Keep it dependency-free,
use source-generated JSON serialization, and keep its files local rather than adding them to sync.
Read [icons.md](../docs/icons.md) and [components.md](../docs/components.md)
before changing its effective name, icon-byte or launch-command comparison.
