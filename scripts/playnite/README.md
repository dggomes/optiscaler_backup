# Playnite local export bridge

**Available from v0.3.5.** A compiled Playnite `GenericPlugin` that exports library metadata and local cover files for OptiScaler Backup's comparison/import flow. It uses pinned PlayniteSDK **6.17.0**, targets .NET Framework **4.6.2**, and runs inside Playnite rather than the backup app. Current Playnite 10.63 is the intended host; live-host acceptance is pending and future Playnite 11 compatibility is not claimed. This is not a PowerShell extension.

## Install

The release includes `optibackup-playnite-bridge.zip` and its SHA-256 checksum. Verify/extract the bridge into its own extension folder:

- Installed Playnite: `%APPDATA%\Playnite\Extensions\OptiScalerBackupBridge`.
- Portable Playnite: `<Playnite installation>\Extensions\OptiScalerBackupBridge`.

`extension.yaml` and `OptiScalerBackup.Playnite.dll` must be directly inside that folder, together with this README. Do not place an extra nested folder between them and the extension directory. The host supplies Playnite SDK assemblies; do not copy SDK DLLs into the extension. Restart Playnite after installation. These locations and the compiled-plugin model follow the [official extension guide](https://api.playnite.link/docs/tutorials/extensions/intro.html) and [plugin introduction](https://api.playnite.link/docs/tutorials/extensions/plugins.html).

Startup/library updates and game install/uninstall events queue exports. For an explicit refresh, choose **OptiScaler Backup → Export library and covers** in Playnite's main menu. Wait for the local file to appear, then open **Playnite…** in OptiScaler Backup:

```text
%LOCALAPPDATA%\OptiScalerBackup\playnite\library.json
%LOCALAPPDATA%\OptiScalerBackup\playnite\covers\
```

The default export does not follow OptiScaler Backup's `--home`; configure/select that JSON path in the app as needed. If no export appears, check that Playnite loaded/enabled the bridge and inspect its log for “OptiScaler library export failed” or “Could not read Playnite library for OptiScaler”. The backup app cannot refresh Playnite itself. See the [complete setup, CLI and comparison guide](../../docs/playnite.md).

## Build from source

From the repository root with the .NET SDK installed:

```powershell
dotnet build scripts/playnite/optiscaler-playnite.csproj -c Release
```

Copy `OptiScalerBackup.Playnite.dll`, `extension.yaml` and this README from the source/build output into the extension folder. The DLL output is under `scripts/playnite/bin/Release/net462`. Build tools/reference assemblies are not runtime extension files. A successful build does not verify loading in a live Playnite instance.

## Boundaries

- `extension.yaml` declares the extension identity, module and `GenericPlugin` type.
- [`bridge.cs`](bridge.cs) reads [IPlayniteAPI.Database](https://api.playnite.link/docs/api/Playnite.SDK.IPlayniteAPI.html) on the event thread and snapshots plain metadata. A serialized background writer copies covers and atomically replaces version 1 UTF-8 `library.json`; superseded queued snapshots can be skipped.
- `optiscaler-playnite.csproj` pins SDK/reference assemblies; Playnite provides the SDK at runtime.

No private database parsing, database modification, game launch/install or network requests occur. HTTP cover URLs are ignored. Local `.jpg`, `.jpeg` or `.png` covers are limited to 4 MiB; unsafe linked paths are rejected and cover-copy failures become export warnings. The consumer additionally checks image signatures, paths and hashes before caching. Old exported covers are not automatically pruned. SDK objects are never accessed by the background file writer.

Before release acceptance, verify real Playnite startup, menu export, library/install/uninstall events, JSON/covers and the app's selected/all import flow. Automated export-writer tests do not replace this host check.
