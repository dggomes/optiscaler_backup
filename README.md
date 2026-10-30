# OptiScaler Backup

Windows x64 CLI for named game-settings snapshots and reversible ZIP package installs. Interactive menus and scriptable commands share a reusable .NET 10 core for a future desktop UI.

This is an independent backup utility, not an official OptiScaler installer. It manages selected game files and settings, not Windows GPU drivers or driver control-panel profiles.

## Run

Download `optibackup-win-x64.exe` from this repository's [GitHub Releases](https://github.com/dggomes/optiscaler_backup/releases), optionally rename it to `optibackup.exe`, and place it in a writable folder outside your game directories. Published builds include the .NET runtime. Release assets become available when a maintainer publishes a version through the release workflow.

Run from PowerShell:

```powershell
.\optibackup.exe
```

The menu scans launchers, lists games, reviews managed files, creates/restores snapshots, imports packages, and checks updates. Game binaries can be nested: select the actual executable, such as a game's `Binaries/Win64` shipping executable, rather than its launcher.

Backups default to `%LOCALAPPDATA%\OptiScalerBackup\backups`. Keep that folder and the catalog safe; they contain the only copies of replaced files retained by this app. See [storage and recovery](docs/storage-format.md).

## Game snapshots

1. Scan installed launchers or add a game manually.
2. Select the game, then **Detect and select managed files**. Review detected graphics libraries, proxy DLLs and settings. Nothing is automatically selected for backup.
3. Add other game-relative paths or explicitly approved settings files outside the game directory. For mods not installed yet, add their expected paths while absent so Original records their absence.
4. Create a named snapshot. Establish **Original** only when you know the current files are clean. Existing mods cannot be converted into pristine originals.
5. Create more named snapshots after changing settings or libraries. Rename labels at any time.
6. Preview and confirm restoration. A recovery snapshot is saved before changing files.

Restores reproduce the selected state for tracked paths, including removing managed files absent from that version. **Currently managed paths missing from the selected snapshot are also treated as absent.** Review removals carefully after expanding your managed-file selection. Unrelated files remain untouched. Removing a path from the current managed list does not remove it from historical snapshots.

Close the game before capture, installation or restoration. Access restrictions are reported; the app does not change permissions. Restoring older DLLs can undo launcher updates, so inspect replacements after a game update.

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

Interactive startup checks for a newer stable release at most once daily and asks before installation. Scripted commands do not run automatic checks. Offline/rate-limit failures do not block normal menu use.

```powershell
.\optibackup.exe update check
.\optibackup.exe update install
.\optibackup.exe config show
.\optibackup.exe config set check-updates false
.\optibackup.exe config set backup-directory (Read-Host 'New central backup folder')
.\optibackup.exe config set max-archive-bytes 2147483648
.\optibackup.exe config set max-archive-files 10000
```

Changing the backup directory copies and verifies payloads before switching the catalog; old copies are retained. Keep backup storage separate from game directories. `--home <folder>` selects a different catalog/data home, useful for isolated use or testing.

The updater downloads this repository's stable Windows executable and SHA-256 checksum over HTTPS, waits for the app to exit, replaces the executable, checks startup health and restarts it. It retains `optibackup.exe.previous` for failed-update rollback. Self-installation requires the published Windows x64 executable named `optibackup.exe` or `optibackup-win-x64.exe` in a writable location. It does not elevate permissions or update while game-file recovery is pending.

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

Publish a self-contained Windows build:

```powershell
dotnet publish src/cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/win-x64
```

Use `artifacts/win-x64/optibackup.exe` on Windows. Custom-folder operations and core tests can run on other operating systems; Windows registry discovery, process checks and executable replacement require Windows.

Node.js is needed only for repository guardrails (`npm run agent:preflight`, `npm run agent:pre-commit`, `npm run agent:session-end`); it is not an application dependency. Windows CI also runs `scripts/test-updater.ps1` against the published executable.

Architecture: `src/core` contains storage, snapshots, transactions, packages and release fetching; `src/windows` contains launcher adapters, detection and updater hosting; `src/cli` provides menus/commands. The core has no UI dependency.

Before a release, verify on a real Windows gaming PC:

- Scan each installed launcher and confirm paths, duplicate handling, nested executables and unavailable drives.
- Capture clean Original, install/mod settings, capture another version, and verify both directions of restoration without changing unrelated files.
- Import a real ZIP, review mappings, install it, edit a managed file, verify rollback stops, then verify recovery capture and override.
- Exercise overlapping package versions and reverse rollback ordering.
- Confirm running-game/locked-file and restricted-folder failures leave recoverable state.
- Verify a real release update, writable-location failure and failed-startup rollback using disposable app/data directories.

Automated tests do not establish compatibility with every installed game or launcher version. Real Windows game installations have not been verified merely by building on macOS.
