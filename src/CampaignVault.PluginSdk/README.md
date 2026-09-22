# CampaignVault.PluginSdk

Contracts and models for writing out-of-tree **CampaignVault** plugins: `IWorldChangeHandler`,
`IChangeContext`, `IInteractionMode`, `PluginManifest`, and the shared domain models
(`Character`, `Location`, `Item`, `WorldChange`, ...) plugins mutate.

This package has no dependency on the CampaignVault host — it's the assembly boundary plugin
DLLs compile against so they never need (or get) access to host internals.

## Usage

```bash
dotnet add package CampaignVault.PluginSdk
```

Implement `IRulesetModule` (data/mechanics plugin) or `IInteractionMode` (turn-based scene
activity), drop the built DLL + a `plugin.json` manifest under `Plugins/<YourPlugin>/` in a
CampaignVault install, and restart the host.

See [PLUGINS.md](https://github.com/myarichuk/CampaignVault/blob/master/PLUGINS.md) in
the main repository for the full plugin architecture, trust model, and quick-start guide.

## License

PolyForm Noncommercial 1.0.0 — see the bundled `LICENSE` file.
