# OptiScaler Backup

Windows x64 utility for named game-settings snapshots and reversible ZIP package installs. The CLI and native WPF desktop interface share the same .NET 10 core and catalog.

This is an independent backup utility, not an official OptiScaler installer. It manages selected game files and settings, not Windows GPU drivers or driver control-panel profiles.

## Run

Download either interface from this repository's [GitHub Releases](https://github.com/dggomes/optiscaler_backup/releases):

- `optibackup-win-x64.exe`: interactive CLI and scriptable commands; optionally rename it to `optibackup.exe`.
- `optibackup-ui-win-x64.exe`: native Windows desktop interface.

Each is a self-contained executable with the .NET runtime included and an accompanying `.exe.sha256` checksum. Place it in a writable folder outside your game directories. Both interfaces are included from v0.2.0 onward.

Version 0.3.1 adds compact portrait library cards and portrait-first thumbnails. The backup format remains schema 2; thumbnail-cache schema 1 is also unchanged.

The examples below assume the executable is named `optibackup.exe`. Run from PowerShell:

```powershell
.\optibackup.exe
```

The menu scans launchers, lists games, reviews managed files, creates/restores snapshots, imports packages, and checks updates. In **Game list**, enter a number or part of a game's name. Name matching ignores case: one match opens the game directly; several matches narrow the list. Enter `*` to clear the filter or `0` to go back.

Game binaries can be nested: select the actual executable, such as a game's `Binaries/Win64` shipping executable, rather than its launcher.

Use the explicit `menu` command when supplying menu input through a pipe. Piped sessions skip startup update checks:

```powershell
.\optibackup.exe menu
@('0') | .\optibackup.exe menu
```

Backups default to `%LOCALAPPDATA%\OptiScalerBackup\backups`. Named versions and recovery snapshots are ordinary full-copy folders beneath `backups\snapshots`, grouped by game and capture time/name. They retain game-relative filenames and separate external-settings folders, so you can browse them in Explorer. CLI snapshot create/list output includes the full `FolderPath`.

Snapshot files are read-only and verified against their recorded hashes before restoration. Do not edit them; modified payloads block restoration before game files change. Full copies use more disk space than deduplicated blobs. Keep the snapshot folders and catalog safe; see [storage and recovery](docs/storage-format.md).

Opening v1 data automatically upgrades it to schema 2, retaining `catalog.v1.json` and old blobs. The upgrade needs space for full copies of every existing snapshot and recovery snapshot. This is a one-way format upgrade: v0.1.x cannot read the upgraded catalog. If conversion fails, the original catalog remains unchanged and the app reports the reason.

## Native Windows interface

Run `optibackup-ui-win-x64.exe` on Windows, or build it using the [desktop instructions](src/desktop/README.md). It opens the same library, snapshots and package history as the CLI. Close other sessions using that catalog before launching it.

- Browse a dark library of compact 2:3 portrait cards, 72 games per page, with **All games**, **With backups** and **No backups** filters. Titles/status occupy one line; hover a card for the full game name. Search partial names across the whole library, ignoring case; clear search or reset filters when needed. Startup scans installed launchers, then checks the entire catalog for thumbnails in the background, including games outside the current page/filter.
- Thumbnail downloads are free and need no API key. Installed Steam IDs are preferred; other titles use a unique exact normalized-name match through Steam's public storefront search, then a Steam CDN image. Some titles have no unique Steam match or available image: locally cached covers, executable icons or initials remain the fallback.
- Use `Ctrl+F` for library search, `Escape` to return from a game/settings page, and `F5` to scan installed launchers. Scan custom folders or add games manually. Cards label unavailable drives; file-changing actions require an available game folder.
- Opening a game detects new standard-file candidates and shows their count. **Files → Review files…** includes current selections and new candidates in one batch, with proxy evidence and external paths. Add existing files or absent relative paths; uncheck files to stop tracking them in future backups. Existing snapshots remain intact.
- The setup guide helps choose files and establish a clean, user-confirmed **Original**. In **Backups**, save/rename versions, preview restoration, or use **Show files** to open the selected folder. Recovery backups are hidden by default; enable **Show recovery backups** to inspect them. Row selections survive refreshes, and selection-dependent actions stay disabled until a row is chosen.
- In **Packages**, review/edit ZIP destinations and exclusions before import, then separately preview installation. Rename packages and roll back active layers with the same overlap ordering and edited-file protection as the CLI.
- **Settings → Change location…** copies and verifies backup storage before switching locations and keeps the old copies. The settings page also opens/copies the backup path and opens app data. **Open backups** opens the current game's snapshot collection when available.

Discovery, hashing and file operations run off the UI thread and are serialized. Startup attempts pending game-file recovery before opening the library. Operation errors appear inline with a dismiss action. **Release downloads** opens GitHub Releases in a browser; desktop self-update is not implemented. Automatic update checks/installations remain CLI-only. Other advanced settings, including archive limits, remain available through CLI `config` commands; close the desktop app before using them.

### Thumbnail downloads and privacy

Downloads are enabled by default. **Settings → Download thumbnails automatically** disables future image requests; **Refresh thumbnails** retries the catalog. Cached images remain usable offline. When no installed Steam ID is available, the game's name is sent to Steam for matching. The search endpoint is public and keyless but undocumented, so availability and rate limits can change. No match is guessed when several exact matches exist.

Images are HTTPS JPEG downloads limited to 4 MiB and verified with SHA-256 in the local cache at `%LOCALAPPDATA%\OptiScalerBackup\thumbnails` (or `<home>/thumbnails` with `--home`). Steam's `library_600x900.jpg` portrait is requested first, with `header.jpg` fallback on 404. Older cached headers get a background portrait attempt without waiting for their normal 30-day refresh; existing images remain usable offline or with downloads disabled. Otherwise successful results are rechecked after 30 days and no-match results after seven days. Offline/service/rate-limit failures pause image downloads without blocking backup, restore or package operations. Changing the backup location does not move this separate cache.

The app's network use is application updates plus optional thumbnail matching/downloads; game-file backup and package operations stay local. To disable thumbnail downloads from the CLI while the desktop app is closed:

```powershell
.\optibackup.exe config set download-thumbnails false
```

## Game snapshots

1. Scan installed launchers or add a game manually.
2. Open the game to automatically detect new standard graphics libraries, proxy DLLs and settings not already managed. The detection flow also offers optional extra game-relative or absolute settings paths.
3. Review the complete proposed batch, including evidence for ambiguous proxy DLLs and full external settings paths, then approve it once. There are no file-by-file approvals, and nothing is added to management before the batch confirmation. **Detect and select managed files** reruns the same flow. For mods not installed yet, include their expected relative paths while absent so Original records their absence.
4. Create a named snapshot. Establish **Original** only when you know the current files are clean. Existing mods cannot be converted into pristine originals.
5. Create more named snapshots after changing settings or libraries. Rename labels at any time.
6. Preview and confirm restoration. A recovery snapshot is saved before changing files.

Restores reproduce the selected state for tracked paths, including removing managed files absent from that version. Paths added to management after capture are preserved: missing metadata does not establish historical absence. Preview lists these unrecorded paths. Unrelated files remain untouched. Removing a path from the current managed list does not remove it from historical snapshots.

Close the game before capture, installation or restoration. Access restrictions are reported; the app does not change permissions. Disconnected or unavailable approved settings roots stop the operation rather than being recorded as absent files. Restoring older DLLs can undo launcher updates, so inspect replacements after a game update.

### Commands

Use the full IDs returned by list/import/create commands. In these PowerShell examples, variables are supplied by you or returned by the app:

```powershell
.\optibackup.exe scan
.\optibackup.exe scan (Read-Host 'Custom game library folder')
.\optibackup.exe games list --json
$game = .\optibackup.exe games add (Read-Host 'Game name') (Read-Host 'Installation folder') | ConvertFrom-Json
$gameId = $game.Id
.\optibackup.exe files detect $gameId --json
.\optibackup.exe files add $gameId (Read-Host 'Game-relative settings or DLL path')
.\optibackup.exe files add $gameId (Read-Host 'Absolute external settings file') --external
.\optibackup.exe snapshots create $gameId Original --original --confirm-clean
.\optibackup.exe snapshots list $gameId --json
$snapshotId = Read-Host 'Snapshot ID to restore'
.\optibackup.exe restore $gameId $snapshotId --dry-run --json
.\optibackup.exe restore $gameId $snapshotId
```

`--confirm-clean` is your assertion, not an automated cleanliness check. Original can be established once per game. General command output is JSON; menus provide human navigation. `--json` explicitly requests the same structured read/preview format. Errors and discovery warnings go to stderr. Run `optibackup help` for the full command list.

## ZIP packages

Packages remain independent from game snapshots. A package contains one named/versioned set of reviewed ZIP files; every installation creates a separate history layer and backup of the exact affected originals.

In the game menu choose **Packages → Import ZIP and optionally install**. For each entry, accept or edit the suggested game-relative destination, or enter `-` to exclude it. Matching prefers existing relative paths, can strip a common wrapper directory, and suggests a unique filename match elsewhere in the game. Multiple matches require a destination or exclusion. Unmatched files still require review of their proposed location.

Importing saves the package without changing game files. Installing previews additions/replacements, backs up original bytes and absent paths, then copies the package files. Bundled scripts/installers are never executed.

For scripting:

```powershell
$zip = Read-Host 'Local ZIP path'
.\optibackup.exe packages inspect $gameId $zip --json
# Create a JSON mapping after reviewing inspect output; see format below.
$map = Read-Host 'Reviewed mapping JSON file'
.\optibackup.exe packages import $gameId $zip (Read-Host 'Package name') (Read-Host 'Version label') --map $map
.\optibackup.exe packages list $gameId --json
$packageId = Read-Host 'Imported package ID'
.\optibackup.exe packages install $gameId $packageId --dry-run
.\optibackup.exe packages install $gameId $packageId
```

A mapping is a JSON object whose keys are the exact `Entry` strings from inspection. Each value is a game-relative destination or `null` to exclude the entry. Every entry needs an explicit mapping. Schema illustration only; substitute your inspected paths:

```json
{
  "<exact ZIP entry path>": "<reviewed game-relative destination>",
  "<excluded ZIP entry path>": null
}
```

Install a newer version by importing it with a new version label and installing that package. Overlapping files require explicit `--allow-overlap`; rollback must undo newer overlapping installations first. Independent packages touching other paths can be rolled back separately.

```powershell
$installationId = Read-Host 'Installation ID from package history'
.\optibackup.exe packages rollback $gameId $installationId --dry-run
.\optibackup.exe packages rollback $gameId $installationId
```

Rollback restores replaced originals and removes files added by that installation. If installed files changed externally, rollback stops for review. `--override-edits` permits replacement only after creating a recovery snapshot. `--yes` skips the final confirmation for scripting; it does not imply overlap or edit overrides. Snapshot restores touching active package paths are blocked until those packages are rolled back.

ZIP imports reject unsafe Windows paths, traversal, links and case-insensitive collisions. Defaults: 2 GiB expanded content and 10,000 entries; configurable below. Inspection caches extracted content but never changes game files.

## Discovery coverage

Discovery uses local installation metadata; no launcher login or online library access is required.

| Launcher | Discovery source |
| --- | --- |
| Steam | Steam registry path, `libraryfolders.vdf`, installed app manifests |
| Epic | Launcher `.item` manifests in ProgramData |
| GOG | GOG Games registry entries |
| EA | EA Games registry entries and Electronic Arts uninstall metadata |
| Ubisoft | Launcher Installs registry entries and Ubisoft uninstall metadata |
| Battle.net | Blizzard/Battle.net uninstall metadata |
| Xbox | `XboxGames` folders on fixed drives, `MicrosoftGame.config`, Gaming Services registry roots |
| Amazon Games | Local `GameInstallInfo.sqlite`, opened read-only through Windows SQLite |

Launcher metadata formats and installation layouts vary. Battle.net games without uninstall registration, Xbox libraries in nonstandard locations, or missing/changed launcher metadata may need custom scanning or manual registration. Restricted Xbox installs remain subject to Windows permissions. Inaccessible scan entries produce warnings. Disconnected installations stay in the catalog as unavailable; rescanning does not delete them.

Custom scans group executable-bearing directories heuristically. Inspect the detected game root and manually add the correct folder if a collection is grouped incorrectly. File walking excludes links and has a bounded depth. Known configuration candidates in Documents/AppData are suggestions based on game/executable names; add unmatched files explicitly. Saves, logs and caches are not selected by detection.

## Updates and configuration

Interactive startup checks for a newer stable release at most once daily and asks before installation. Scripted commands, including piped `menu` sessions, do not run automatic checks. Offline/rate-limit failures do not block normal menu use.

```powershell
.\optibackup.exe update check
.\optibackup.exe update install
.\optibackup.exe config show
.\optibackup.exe config set check-updates false
.\optibackup.exe config set download-thumbnails false
.\optibackup.exe config set backup-directory (Read-Host 'New central backup folder')
.\optibackup.exe config set max-archive-bytes 2147483648
.\optibackup.exe config set max-archive-files 10000
```

Changing the backup directory copies and verifies referenced snapshot files and blobs before switching the catalog, preserves snapshot read-only attributes, and retains old copies. Keep backup storage separate from game directories. `--home <folder>` selects a different catalog/data home, useful for isolated use or testing.

The updater downloads this repository's stable Windows executable and SHA-256 checksum over HTTPS, waits for the app to exit, replaces the executable, checks startup health and restarts it. It retains a `.previous` copy beside the executable for failed-update rollback. Self-installation requires the published Windows x64 executable named `optibackup.exe` or `optibackup-win-x64.exe` in a writable location. It does not elevate permissions or update while game-file recovery is pending.

Checksums detect corruption; they are published beside the executable and are not independent signatures or protection against a compromised release account. There is no automatic mod download or system-driver installation.

## Build and validation

Install the .NET 10 SDK. No third-party NuGet dependencies are required by the application.

```powershell
dotnet build optiscaler-backup.sln -c Release
dotnet run --project tests -c Release
python3 scripts/test-cli.py src/cli/bin/Release/net10.0/optibackup.dll
dotnet run --project src/cli -- help
dotnet run --project src/cli
```

Optional live thumbnail verification makes actual requests to Steam and checks title matching, HTTPS image download and persistent cache integrity:

```powershell
dotnet run --project tests -- --live-thumbnails
```

Publish a self-contained Windows build:

```powershell
dotnet publish src/cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/win-x64
```

Use `artifacts/win-x64/optibackup.exe` on Windows. Custom-folder operations and core tests can run on other operating systems; Windows registry discovery, process checks and executable replacement require Windows.

Build/publish the desktop app separately; it is excluded from the cross-platform solution:

```powershell
dotnet build src/desktop/optiscaler-backup-desktop.csproj -c Release
dotnet publish src/desktop/optiscaler-backup-desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/desktop
```

Run `artifacts/desktop/optibackup-ui.exe` on Windows. WPF cannot run on macOS/Linux. Windows CI includes startup/render smoke coverage; a cross-build alone does not verify runtime behavior or real-game compatibility.

Node.js is needed only for repository guardrails (`npm run agent:preflight`, `npm run agent:pre-commit`, `npm run agent:session-end`); it is not an application dependency. Windows CI also runs `scripts/test-updater.ps1` against the published executable.

Architecture: `src/core` contains storage, snapshots, transactions, packages and release fetching; `src/windows` contains launcher adapters, detection, thumbnail fetching/cache and updater hosting; `src/cli` provides menus/commands; `src/desktop` provides WPF windows, background thumbnail coordination and review dialogs. The core has no UI dependency.

Before a release, verify on a real Windows gaming PC:

- Scan each installed launcher and confirm paths, duplicate handling, nested executables and unavailable drives.
- Capture clean Original, install/mod settings, capture another version, and verify both directions of restoration without changing unrelated files.
- Import a real ZIP, review mappings, install it, edit a managed file, verify rollback stops, then verify recovery capture and override.
- Exercise overlapping package versions and reverse rollback ordering.
- Confirm running-game/locked-file and restricted-folder failures leave recoverable state.
- Verify a real release update, writable-location failure and failed-startup rollback using disposable app/data directories.

Automated tests do not establish compatibility with every installed game or launcher version. Real Windows game installations have not been verified merely by building on macOS.
