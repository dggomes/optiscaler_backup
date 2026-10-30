# Native Windows interface

WPF frontend for the shared .NET 10 core. It uses the same catalog and backups as the CLI, including package installation history. Only one session may open a data home at a time; close the CLI before starting the desktop app.

## Build and run

Download the self-contained `optibackup-ui-win-x64.exe` and its `.exe.sha256` checksum from [GitHub Releases](https://github.com/dggomes/optiscaler_backup/releases), available from v0.2.0. Run the executable on Windows; no .NET SDK/runtime installation is required for release builds.

The desktop project is deliberately outside `optiscaler-backup.sln`, keeping core/CLI solution builds cross-platform. Install the .NET 10 SDK, then on Windows:

```powershell
dotnet build src/desktop/optiscaler-backup-desktop.csproj -c Release
dotnet run --project src/desktop/optiscaler-backup-desktop.csproj
```

Publish a self-contained Windows x64 executable:

```powershell
dotnet publish src/desktop/optiscaler-backup-desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/desktop
.\artifacts\desktop\optibackup-ui.exe
```

`--home <folder>` selects a different catalog home. WPF runs only on Windows; macOS/Linux cross-building does not verify startup or rendering. Windows CI includes startup/render smoke coverage; this does not establish compatibility with every game or launcher.

## Workflow

Search filters game names immediately, ignoring case. Scan installed launchers, scan a custom folder or add a game manually. Selecting a game detects standard-file candidates and reports how many are new. **Review managed files** presents one editable selection batch with proxy evidence and external paths; add existing files or absent game-relative paths, then approve once.

**Versions** captures named snapshots, offers user-confirmed Original once, renames labels/folders and previews restoration before confirmation. **Show files** opens the selected snapshot's browsable full-copy folder. Files are read-only; edits are detected before restoration. Renaming a snapshot uses a recovery journal so an interrupted rename can be rolled back safely.

**Packages** reviews ZIP destinations/exclusions, imports named versions, previews installs and rolls back active installations. Package payloads remain deduplicated blobs; their pre-install/recovery backups are browsable snapshot folders. Overlapping layers require explicit approval and reverse rollback order; externally edited files require explicit override after recovery capture. The engine preserves these rules across both interfaces.

**Open backups** opens the selected game's snapshot collection when it exists, otherwise the configured payload folder. **Release downloads** opens the repository's releases page; the desktop app does not automatically check for or install updates. Use CLI `config` for advanced settings after closing the desktop app.

First opening a schema 1 catalog converts existing snapshots to schema 2 folders, retaining `catalog.v1.json` and old blobs. Allow extra disk space for full copies. Schema 2 is not readable by v0.1.x. See [storage and recovery](../../docs/storage-format.md) before moving or restoring the data home.

## Boundaries

- `app.cs`: WPF entrypoint, catalog lock, startup recovery and startup/render smoke entrypoint.
- `main-window.xaml` / `.xaml.cs`: library/search, actions and serialized background work; closing is blocked during active work.
- `dialogs.cs`: managed-file batch selection, labels/Original confirmation, ZIP mappings and change/conflict previews.

Backup formats, path validation and recoverable mutations belong to the core. Windows discovery/process checks belong to the Windows adapter project. The desktop layer invokes those services and collects explicit user choices.
