# Playnite library integration

**Upcoming v0.3.5; not included in v0.3.4.** This integration reads a local export produced inside Playnite through its SDK. It compares that library with launcher scans, imports selected installed games and optionally caches explicitly exported local covers. It never edits Playnite's database, installs games or changes game files. Live Playnite 10-host acceptance remains pending; future Playnite 11 compatibility is not claimed.

## Install the bridge

The bridge must be installed manually into Playnite; launching OptiScaler Backup does not install it. See the [bridge build/install instructions](../scripts/playnite/README.md). For a normal Playnite installation, the extension folder belongs beneath `%APPDATA%\Playnite\Extensions`; for portable Playnite, beneath its installation's `Extensions` directory. Each extension needs its own folder and `extension.yaml`, as described by the [official extension guide](https://api.playnite.link/docs/tutorials/extensions/intro.html).

After installation, restart Playnite or refresh its library to produce a fresh export. You can also use Playnite’s **OptiScaler Backup → Export library and covers** main-menu action. Startup, library updates and game install/uninstall events automatically queue an export. The default export is:

```text
%LOCALAPPDATA%\OptiScalerBackup\playnite\library.json
%LOCALAPPDATA%\OptiScalerBackup\playnite\covers\
```

The bridge writes only its own export and local cover copies. It reads game metadata through [IPlayniteAPI.Database](https://api.playnite.link/docs/api/Playnite.SDK.IPlayniteAPI.html), rather than parsing private database files. No network key or account is required. Existing local Playnite covers can be exported; remote cover URLs are not downloaded by the bridge.

## Desktop steps

1. Scan launchers normally if you want a useful comparison with existing detections.
2. Choose **Playnite…**, or **Settings → Playnite → Compare / import…**. Use **Choose export…** if the JSON is elsewhere.
3. Check the displayed export time. The app reads that file; it does not ask Playnite to refresh or reject an old export automatically.
4. Review totals and rows. Exclude platform, tag or category names if needed, separated by `;`. Names match literally, ignoring case; use the labels actually exported by Playnite. Editing filters clears selections.
5. Select individual eligible installed entries or **Select all eligible**. **Clear selection** resets the choices.
6. Choose **Save filters / import selected**. With nothing selected, only the export path and filters are saved. Review any warnings afterward.

Import attaches to an existing same-folder catalog entry or creates one. An inaccessible/missing installation is skipped with a warning. A bad cover does not undo an otherwise imported game, and import processes entries incrementally. Imported local covers take priority over later Steam refreshes. They do not become game snapshots or package payloads.

## CLI steps

Close the desktop app before opening the same catalog from the CLI. These PowerShell variables come from your machine and command output, not example IDs:

```powershell
$export = Join-Path $env:LOCALAPPDATA 'OptiScalerBackup\playnite\library.json'
.\optibackup.exe playnite compare $export --json
$platforms = Read-Host 'Exact platform labels to exclude, separated by ;'
.\optibackup.exe playnite configure $export --exclude-platform $platforms --yes
.\optibackup.exe playnite compare --json
.\optibackup.exe playnite import $export all --dry-run
.\optibackup.exe playnite import $export all --yes
$playniteId = Read-Host 'Playnite ID from the comparison rows'
.\optibackup.exe playnite import $export $playniteId --dry-run
.\optibackup.exe playnite import $export $playniteId --yes
```

`--exclude-platform`, `--exclude-tag` and `--exclude-category` accept semicolon-separated lists; pass an empty string to clear a list. **Configure** persists the export path and supplied filters, with confirmation (`--yes` for scripts). **Compare** defaults to the saved path or bridge default and supports temporary filter overrides. **Import** requires a path plus `all` or one exported Playnite ID; its filter overrides are temporary unless saved separately using **configure**. `--dry-run` previews selections without importing; `--yes` approves mutation. Catalog game IDs and Playnite IDs are different identifiers.

## Comparison and import rules

Matching uses normalized absolute installation paths, case-insensitively, not titles. A usable absolute path plus Playnite's installed flag makes an entry eligible unless filtered or already excluded; the folder must also exist and pass link/storage-overlap checks during import. Matching by path preserves existing snapshots/history. Duplicate Playnite entries at one path can attach multiple Playnite IDs to one catalog game.

The report distinguishes:

| Count | Meaning |
| --- | --- |
| Playnite total / filtered / included | Export entries before/after configured metadata filters |
| Installed | Included entries whose exported installed flag is true |
| Matched | Included, non-excluded installed entries with a catalog path match |
| Missing installed | Entries missing from the scan, or installed entries without a usable absolute path |
| Not installed | Unfiltered, non-excluded entries without the installed flag |
| Game exclusions | Entries matched to a permanently excluded catalog game |
| Scanned total | All visible catalog games, independent of Playnite filters |
| Scanned filtered out | Visible catalog games matched only by filtered Playnite entries |
| Scanned only | Visible games with neither an included Playnite match nor a filtered-only counterpart |

Playnite counts are **per export entry**, while scanned counts are per catalog game. Installed overlaps other counts; statuses apply filter/exclusion precedence, so totals are not a single disjoint partition. An included uninstalled entry can still provide a path match for scanned-only accounting. Duplicate entries, emulator/shared installation folders, inaccurate installed flags, unavailable drives and platform labels such as distinct PC/Windows names can explain differences. No title-based or platform-alias guessing reconciles them.

## Reversible game exclusions

**Remove and exclude…** or `games exclude <game-id>` hides the catalog game and prevents discovery from re-adding that installation path. Original, named/recovery snapshots, packages and active installation history stay intact. Restore visibility through **Settings → Excluded games → Restore** or `games unexclude <game-id>`; no game files are restored/reinstalled. `games excluded --json` lists retained entries. These are catalog IDs, not Playnite IDs.

Playnite platform/tag/category filters only change comparison/import eligibility. They do not hide already scanned games or set permanent exclusions. Explicitly excluded games cannot be imported until unexcluded.

## Export format and bounds

The local JSON has independent `Version: 1`, UTC `ExportedUtc`, `Games` and `Warnings`. Each game contains a GUID `Id`, `Name`, optional `InstallDirectory`, Boolean `Installed`, optional export-relative `Cover`, and `Platforms`, `Tags`, `Categories` string arrays. The reader requires unique IDs, names up to 160 characters with no controls, and nonempty metadata labels up to 200 characters. It rejects unsupported versions, exports over 32 MiB and over 50,000 games without rewriting the export.

Cover destinations must remain within the export directory; traversal, rooted paths and linked ancestors are rejected. Import accepts only JPEG/PNG signatures, at most 4 MiB, and records SHA-256 in the independent thumbnail cache. This is an image import, never executable content. No universal artwork coverage is promised. Default export location does not follow the app's `--home`; configure/select the export path explicitly when using another home. Backup relocation does not move the export or thumbnail cache. See [storage format](storage-format.md).

## Windows acceptance still required

On a real Playnite 10 installation, verify bridge startup/library-refresh export, installed/uninstalled updates and local JPEG/PNG covers. Compare scans against duplicates, filtered platform/tag/category names, excluded entries and an unavailable installation; select one game, then all eligible entries. Confirm imported IDs/covers, retained backup history, exclusion/unexclude behavior and useful warnings. Automated reader/import tests do not establish that the bridge has loaded successfully in a live Playnite host.
