# Native Windows interface

WPF frontend for the shared .NET 10 core. It uses the same catalog and backups as the CLI, including package installation history. Only one session may open a data home at a time; close the CLI before starting the desktop app.

Version 0.3.1 uses a compact portrait library and guided game workflows, informed by [OptiClick’s published game-first interface](https://github.com/onehoon/OptiClick). Backup schema 2 and thumbnail-cache schema 1 remain unchanged; this release does not add another storage migration.

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

## Library and navigation

The library displays 72 compact cards per page: 112-pixel widths, 168-pixel artwork (2:3), and 10-pixel gutters. Title/status rows stay on one line; truncated names remain available in the card tooltip. Search matches partial game names across every page, ignoring case. Use **All games**, **With backups** or **No backups** filters; clear search or reset the filters from an empty result. Click a card or select it and press Enter to open the game.

At startup, the app scans installed launchers and queues thumbnail checks for the entire catalog, across all pages and filters. Background downloads use installed Steam manifest IDs where available; otherwise the game's title is sent to Steam's public storefront search. Only a unique exact normalized-title match is accepted. The provider is free/keyless and uses HTTPS Steam CDN JPEG images, but its search endpoint is undocumented and may change or rate-limit requests.

Steam downloads request `library_600x900.jpg` portraits first and try `header.jpg` only on 404. Not every game exists on Steam or has a unique match/image. Downloaded thumbnails fall back to locally cached Steam covers, executable icons or name initials. Offline/service/rate-limit failures pause only the download queue; games and local backup operations remain usable. Unavailable game drives are labeled on cards and the game page.

- `Ctrl+F`: return to the library and focus search.
- `Escape`: return from game/settings to the library.
- `F5`: scan installed launchers.

The library toolbar also scans a custom folder or adds a game manually. Library, game and settings are separate pages; changing the library search does not discard the current game.

## Game workflows

Opening a game detects standard-file candidates and reports how many are new. **Files → Review files…** presents current managed files and new candidates together, with proxy evidence and external paths. Add existing files or absent game-relative paths, uncheck files to stop tracking them for future captures, then approve once. Untracking does not alter historical snapshots.

The setup guide offers file selection first, then **Save Original…** when no Original exists. The clean/unmodified assertion requires explicit confirmation. **Save backup** captures another named snapshot. **Backups** supports rename and restore previews; recovery snapshots are hidden until **Show recovery backups** is checked. Snapshot, package and installation selections persist through refreshes. Buttons needing a selected row are disabled until one is chosen; capture, install, restore and rollback also require an available game folder.

**Show files** opens the selected snapshot's browsable full-copy folder. Files are read-only; edits are detected before restoration. Renaming uses a recovery journal so an interrupted rename can be rolled back safely.

**Packages** reviews ZIP destinations/exclusions, imports named versions, previews installs and rolls back active installations. Package payloads remain deduplicated blobs; their pre-install/recovery backups are browsable snapshot folders. Overlapping layers require explicit approval and reverse rollback order; externally edited files require explicit override after recovery capture. The engine preserves these rules across both interfaces.

The game workspace scrolls in compact windows to keep package tables and their actions reachable. Operations run off the UI thread, one at a time. Active work temporarily disables competing actions and prevents closing the window; errors are shown inline and can be dismissed. Startup recovery runs before normal work.

## Settings and storage

**Settings → Change location…** confirms the source/destination, copies and verifies snapshots/blobs, then switches the configured location. Old copies are retained. Other controls open the backup folder, copy its path or open app data. **Open backups** from a game opens that game's snapshot collection when it exists; elsewhere it opens the configured payload folder.

**Download thumbnails automatically** is enabled by default; switch it off to stop future matching/download requests. Existing cached images remain available. **Refresh thumbnails** retries every catalog entry. Images live under `<home>/thumbnails`, separate from backup storage, with 4 MiB JPEG limits and SHA-256 verification. Successful entries are rechecked after 30 days; no-match entries after seven days. Moving backup storage does not relocate this cache. The same preference is available through CLI `config set download-thumbnails false` after closing the desktop app.

Older cached headers receive a background portrait attempt even when still within the 30-day window. Existing images remain visible offline or when downloads are disabled. Cache metadata records that a portrait was attempted, not that the final image is necessarily a portrait; a missing portrait may still use the header fallback.

**Release downloads** opens the repository's releases page; the desktop app does not automatically check for or install updates. Use CLI `config` for other advanced settings, such as archive limits, after closing the desktop app.

First opening a schema 1 catalog converts existing snapshots to schema 2 folders, retaining `catalog.v1.json` and old blobs. Allow extra disk space for full copies. Schema 2 is not readable by v0.1.x. See [storage and recovery](../../docs/storage-format.md) before moving or restoring the data home.

## Boundaries

- `app.cs`: WPF entrypoint, catalog lock, startup recovery and startup/render smoke entrypoint.
- `main-window.xaml` / `.xaml.cs`: library/search, pagination, selection state and serialized background work.
- `navigation.cs` / `game-actions.cs`: library/settings navigation, storage relocation and game backup/package workflows.
- `game-artwork.cs`: cached-download/local Steam cover and executable-icon loading with initials supplied by the library.
- `thumbnails.cs`: catalog-wide background download scheduling, progress, retry and opt-out; matching/cache integrity belongs to the Windows adapter service.
- `theme.xaml` / `window-chrome.cs`: shared dark controls and native window styling.
- `dialogs.cs`: managed-file batch selection, labels/Original confirmation, ZIP mappings and change/conflict previews.

Backup formats, path validation and recoverable mutations belong to the core. Windows discovery/process checks belong to the Windows adapter project. The desktop layer invokes those services and collects explicit user choices.

The optional `dotnet run --project tests -- --live-thumbnails` check contacts the real provider to validate matching/download/cache behavior on supported .NET hosts. It does not exercise WPF rendering or verify real Windows game installations.
