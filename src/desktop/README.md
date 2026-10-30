# Native Windows interface

WPF frontend for the shared .NET 10 core. It uses the same catalog and backups as the CLI, including package installation history. Only one session may open a data home at a time; close the CLI before starting the desktop app.

Version 0.3.2 recovers stale session markers automatically after exclusive acquisition and reports the data folder plus active Windows holder name/PID when available. Briefly closing sessions are retried; active sessions are never bypassed. Close the owner normally or let its update finish rather than deleting the lock. Read-only recovery changes only the stale marker's attribute; other access failures require fixing data-folder access. See [startup troubleshooting](../../README.md#startup-reports-sessionlock).

Version 0.3.1 uses a compact portrait library and guided game workflows, informed by [OptiClick’s published game-first interface](https://github.com/onehoon/OptiClick), plus shared CLI/desktop self-updates. Backup schema 2 and thumbnail-cache schema 1 remain unchanged; this release does not add another storage migration.

## Build and run

Download the self-contained `optibackup-ui-win-x64.exe` and its `.exe.sha256` checksum from [GitHub Releases](https://github.com/dggomes/optiscaler_backup/releases), available from v0.2.0. Run the executable on Windows; no .NET SDK/runtime installation is required for release builds.

Users retaining the older v0.3.0 interface can manually download the separate v0.3.0.1 lock-only hotfix. That classic line does not gain the newer shared desktop updater or later UI behavior; the rest of this document describes the current line.

Keep it beside `optibackup-win-x64.exe` to update both interfaces together. Short names `optibackup-ui.exe` and `optibackup.exe` are also supported. Only recognized executables already in that folder are updated; copies elsewhere and uninstalled components are excluded.

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

The library toolbar also scans a custom folder or adds a game manually. Launcher and folder scans run in the background while the app remains usable; their catalog merge waits for an active file operation to finish. Library, game and settings are separate pages; changing the library search does not discard the current game.

## Game workflows

Opening a game detects standard-file candidates and reports how many are new. **Files → Review / add files…** presents current managed files and new candidates together, with proxy evidence and external paths. Rules include OptiScaler ASI/ReShade files, graphics-library families and companion configuration formats. Add existing files or absent game-relative paths, adjust the manual selection, then approve once. Historical targets and inventory-detected changes can still be included after unchecking; historical snapshots remain intact.

The **Files** table separates **File name**, **Folder** and **Location**; game folders are relative, external settings retain their external folder. Hover filename/folder cells for the full path. **Filter files…** searches paths case-insensitively and shows matching/total counts; opening another game clears the filter.

The setup guide offers file selection first, then **Save Original…** when no Original exists. Original setup rechecks missing detected files and offers batch review even if a regular backup already exists; external paths are never approved automatically. The clean/unmodified assertion requires explicit confirmation. Before capture, the app inventories the entire game folder's regular files/hashes and folders, excludes links, and shows a **Review backup** confirmation with inventory/target counts and paths. Initial capture copies approved target bytes only; later snapshots compare against Original's inventory or the earliest available inventory, copy changed/new files, record deletions and carry prior targets so versions remain self-contained. Original cannot recover unselected original bytes; it removes only later-tracked additions proven absent by its inventory. Full-game hashing can take time, and changed game assets or game-local saves/logs/caches can increase backup size.

**Backups** supports rename and restore previews; recovery snapshots are hidden until **Show recovery backups** is checked. Snapshot, package and installation selections persist through refreshes. Buttons needing a selected row are disabled until one is chosen; capture, install, restore and rollback also require an available game folder.

**Show files** opens the selected snapshot's browsable full-copy folder. Files are read-only; edits are detected before restoration. Renaming uses a recovery journal so an interrupted rename can be rolled back safely.

**Packages** reviews ZIP destinations/exclusions, imports named versions, previews installs and rolls back active installations. Package payloads remain deduplicated blobs; their pre-install/recovery backups are browsable snapshot folders. Overlapping layers require explicit approval and reverse rollback order; externally edited files require explicit override after recovery capture. The engine preserves these rules across both interfaces.

Game pages hide the global app masthead in favor of a compact title, library breadcrumb, counts, path and Original reminder. The header stays above tabs that fill the remaining window height. Tables scroll internally; there is no whole-game-page scroll. Compact 30-pixel rows, controls and review dialogs keep more files visible while retaining the clean-Original confirmation.

Inventory capture and file mutations run off the UI thread, with mutations serialized. Active file work temporarily disables competing actions and prevents closing the window; background library scans remain separate and defer their merge when needed. Dialog values are captured on the UI thread before worker tasks use them, avoiding cross-thread dialog access. Errors are shown inline and can be dismissed. Startup recovery runs before normal work.

## Settings and storage

**Settings → Change location…** confirms the source/destination, copies and verifies snapshots/blobs, then switches the configured location. Old copies are retained. Other controls open the backup folder, copy its path or open app data. **Open backups** from a game opens that game's snapshot collection when it exists; elsewhere it opens the configured payload folder.

Settings places storage and thumbnail controls in the left column and update actions in the right, keeping related actions together without a long stacked page.

**Download thumbnails automatically** is enabled by default; switch it off to stop future matching/download requests. Existing cached images remain available. **Refresh thumbnails** retries every catalog entry. Images live under `<home>/thumbnails`, separate from backup storage, with 4 MiB JPEG limits and SHA-256 verification. Successful entries are rechecked after 30 days; no-match entries after seven days. Moving backup storage does not relocate this cache. The same preference is available through CLI `config set download-thumbnails false` after closing the desktop app.

Older cached headers receive a background portrait attempt even when still within the 30-day window. Existing images remain visible offline or when downloads are disabled. Cache metadata records that a portrait was attempted, not that the final image is necessarily a portrait; a missing portrait may still use the header fallback.

**Check for updates at startup** uses the same enabled/disabled preference and once-daily schedule as the CLI. **Check updates** performs a manual check; an available update is shown in settings and a banner. **Install update…** presents the release version, notes and exact executable targets before confirmation, download and restart. **Release downloads** still opens GitHub Releases in a browser. Offline checks do not block game operations. Use CLI `config` for other advanced settings, such as archive limits, after closing the desktop app.

The shared updater downloads the installed same-folder components from one release and checks each checksum independently. Close the companion interface before installation. Pending game/rename recovery or unwritable destinations block updating. Previous executables are retained as `.previous`; every updated component is health-checked, including desktop WPF runtime/theme loading. Failure or interrupted replacement restores the entire group. The initiating interface restarts using the same data home. Published Windows builds can self-install; development builds can check releases only.

First opening a schema 1 catalog converts existing snapshots to schema 2 folders, retaining `catalog.v1.json` and old blobs. Allow extra disk space for full copies. Schema 2 is not readable by v0.1.x. See [storage and recovery](../../docs/storage-format.md) before moving or restoring the data home.

## Boundaries

- `app.cs`: WPF entrypoint, catalog lock, startup recovery, shared updater helper/health entrypoints and startup/render smoke entrypoint.
- `main-window.xaml` / `.xaml.cs`: library/search, pagination, selection state and serialized background work.
- `scanning.cs`: background discovery/file analysis, cancellation and deferred catalog merging.
- `navigation.cs` / `game-actions.cs`: library/settings navigation, storage relocation and game backup/package workflows.
- `game-artwork.cs`: cached-download/local Steam cover and executable-icon loading with initials supplied by the library.
- `thumbnails.cs`: catalog-wide background download scheduling, progress, retry and opt-out; matching/cache integrity belongs to the Windows adapter service.
- `updates.cs`: shared daily/manual release checks, opt-out, release-note/target confirmation and staged update/restart.
- `theme.xaml` / `window-chrome.cs`: shared dark controls and native window styling.
- `dialogs.cs`: managed-file batch selection, labels/Original confirmation, ZIP mappings and change/conflict previews.

Backup formats, path validation and recoverable mutations belong to the core. Windows discovery/process checks belong to the Windows adapter project. The desktop layer invokes those services and collects explicit user choices.

The optional `dotnet run --project tests -- --live-thumbnails` check contacts the real provider to validate matching/download/cache behavior on supported .NET hosts. It does not exercise WPF rendering or verify real Windows game installations.
