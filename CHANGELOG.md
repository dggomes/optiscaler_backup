# Changelog

## [0.3.5] — 2026-10-31

### Added

- Reversible library exclusions in desktop and CLI. Hidden installations stay excluded from future scans while their game files, backups, Original snapshots, packages and history are preserved; restore them from Excluded games.
- Local Playnite SDK bridge exporting games, covers, platforms, tags and categories. Compare the export with the scanned library, filter by platform/tag/category, then import individual or all eligible installed games and their covers.
- Playnite comparison counts distinguish matches, missing installations, uninstalled entries, filters and game exclusions without replacing existing backup history.
- Compare two recorded backup versions in desktop or with `snapshots compare`. Reports show Added, Removed, Changed, Unchanged and Unknown coverage states, before/after SHA-256 hashes, sizes and capture coverage.
- Desktop comparison includes change filters, path search and a swap action for reversing the comparison direction.

### Changed

- Resolve Steam's newer hash-based cover paths through free, keyless Steam StoreBrowse asset metadata. Prefer portrait assets, retain bounded image validation and use limited platform/edition title aliases only when a unique match exists.
- Retry old cached thumbnail misses with the updated lookup and retry new misses daily. Isolate per-game artwork failures so one failed request does not stop the remaining catalog.
- Remove executable-icon and implicit local Steam-cache artwork fallbacks. Library cards use cached API artwork or explicitly imported Playnite covers, with initials when no cover is available.

### Fixed

- Retry Windows executable replacement briefly during update installation and rollback when transient locks linger after health checks. Retries are bounded to two seconds; persistent access errors still fail with path guards, recovery journals and existing permissions preserved.

Backup comparison uses recorded file states and inventories without reading live game files. Coverage labels describe what was captured, not payload-integrity verification. Missing legacy/external metadata and excluded-link paths remain unknown rather than being reported as deletions.

Backup-comparison unit and native UI validation are in progress. Automated service, CLI, native desktop, compiled bridge export and updater checks gate publication. Live Playnite-host and real gaming-PC acceptance remain pending.

## [0.3.4] — 2026-10-31

### Added

- Read-only inspection of captured `OptiScaler.ini` preferences after backup-payload hash verification, plus current OptiScaler settings from approved managed paths.
- Separate game graphics and OptiScaler settings tabs, retaining saved/raw values, file/line/layer, source references and interpretation confidence.
- Recognize documented game DLSS, FSR and XeSS upscaling/frame-generation enable preferences, including DLAA and raw plugin-version-specific quality values.
- Show OptiScaler's API-specific DX11/DX12/Vulkan upscaler choices, frame-generation enable/input/output and NVNGX replacement preferences, plus legacy `FGType` and OptiFG enable keys.

Automatic/default choices remain unspecified; custom, unknown and game-specific numeric modes remain raw or unmapped rather than guessed. Saved game preferences and OptiScaler middleware choices are reported separately and do not establish runtime activation or the selected FSR version.

Validation passed in [Windows/Linux CI](https://github.com/dggomes/optiscaler_backup/actions/runs/41195734792): 68 Windows service tests, 66 service tests with two Windows-only skips on Linux/macOS, CLI end-to-end and archive-policy checks. Native game/OptiScaler settings tabs rendered successfully, and updater checks passed. Real gaming-PC acceptance testing remains pending.

## [0.3.3] — 2026-10-31

### Added

- Cumulative component fingerprint catalog for automatic backup identification in CLI and desktop ([#15](https://github.com/dggomes/optiscaler_backup/issues/15)). The bundled offline catalog contains 98 real records from signed DLSSync data and official OptiScaler/OptiPatcher archives, including FSR builds `4.1.0.0`, `4.1.1.2740` and `4.1.2.4555`.
- SHA-256 matching identifies renamed component files while retaining unknown/custom builds and multiple possible matches. Historical backups are identified without rewriting them; reports distinguish saved file bytes from inventory-only evidence.
- Small data-only catalog refreshes retain older fingerprints so previously recognized component versions remain identifiable.
- Read-only inspection of saved and current approved Unreal INI graphics preferences ([#16](https://github.com/dggomes/optiscaler_backup/issues/16)): resolution, window mode, render scales, VSync, HDR and documented FSR quality preferences, including Native AA, Quality and Performance.
- Desktop component/settings details and a backup component-summary column, with CLI component identification and settings reports. Reports retain source references, file/layer, raw values, interpretation confidence and conflicting-value warnings.

Saved preferences and component presence do not establish runtime enablement. A 100% render scale is not proof of Native AA or DLAA; game-specific numeric DLSS values remain unmapped. Inventory hashes do not reconstruct uncaptured settings bytes.

Validated on Windows and Linux: 67 Windows service tests (65 plus two Windows-only skips on Linux/macOS), CLI/menu round trips, producer archive-policy tests and native UI inspection using official FSR/OptiPatcher byte fixtures and captured Unreal settings. Native CLI and paired updater recovery checks pass. Real gaming-PC acceptance remains pending.

## [0.3.2] — 2026-10-30

### Fixed

- Recover stale `session.lock` markers at startup only after acquiring the exclusive OS lock, then remove the marker when the session closes. Read-only leftovers are recovered only after an exclusive read guard succeeds; live old or new app sessions cannot be bypassed.
- Retry briefly during app/updater restarts and recheck lock paths for links or junctions before recovery.
- Show lock-holder application names and PIDs in Windows CLI/desktop errors using Restart Manager, without forcing another process to close.
- Compare and display all four version components when present, including `0.3.0.1`, so companion repair, downgrade protection and updater health checks do not truncate revision numbers.

### Added

- Development foundation for the future manager: independently implemented immutable contracts for source provenance, game analysis, resolved components and file-operation plans.
- Upstream source registry distinguishing released OptiPatcher compatibility, preview/main observations, the official OptiScaler wiki and reference-only repositories.
- Bounded, SHA-256-verified, display-only JSON compatibility-pack format 1 with separate `data-v` tags, strict field validation and source evidence checks.
- Conservative matching by exact launcher ID or a unique exact normalized title, preserving edition distinctions, conflicting observations and Unknown / Not applicable support states.

This is the first development slice of the manager work ([#12](https://github.com/dggomes/optiscaler_backup/issues/12), under [#9](https://github.com/dggomes/optiscaler_backup/issues/9)), not the full Phase A implementation. CLI/UI integration, feed refresh or activation, component installation and schema 3 are not included.

Local validation: 61 service tests pass with two Windows-only skips, plus CLI/menu checks. Windows CI also covers native CLI/WPF startup with stale read-only markers, live-owner reporting and updater recovery. Real gaming-PC acceptance remains pending.

## [0.3.1] — 2026-10-30

### Added

- Desktop self-update with a daily startup check, update banner, manual check/install controls and release-note confirmation. CLI and desktop share the automatic-check preference and timestamp; automatic checks can be disabled.
- Update installed CLI/UI companions together when their recognized executable names are in the same folder. Download and verify every component from the same GitHub release; absent companions are not installed.
- Paired updater jobs retain `.previous` executable copies, wait for the initiating app to exit, require idle storage and block running companions. Each updated component receives a health check, including WPF resource startup; failures or interruptions restore the recorded previous executables.
- Preserve custom data-home paths on restart and archive completed updater jobs before restarting older binaries. Paired job format 2 retains compatibility with legacy single-CLI format 1 jobs.
- Repair an older companion from the current stable release without downgrading a companion that is already newer.
- Managed-file filtering and visible file counts, with separate filename/folder columns and full-path tooltips in the workspace and review dialogs.
- Inventory-assisted backups record all game file paths, folder paths and file hashes without copying the entire game. Original stores the selected file bytes; later backups also include changed/new files, recorded deletions and previously tracked paths after review.
- Original restoration removes later tracked additions only when its inventory proves those paths were absent. Inventory hashes cannot reconstruct original bytes of files that were not selected for backup.

### Changed

- Compact library cards use 112 × 168 portrait cover areas with 10 px gutters, a single-line title and status, and full game names in tooltips.
- Increase the library page size from 24 to 72 games.
- Request Steam's real `library_600x900.jpg` portrait artwork first; fall back to `header.jpg` only when the portrait is unavailable (HTTP 404).
- Automatically refresh older landscape thumbnail caches using an optional `PortraitChecked` metadata field. Existing covers remain available offline; no catalog or cache-schema migration is required.
- Reclaim game-workspace space by hiding repeated app branding, compacting the game header and Original guide, and letting tabs and tables fill the remaining window. File tables scroll internally instead of scrolling the entire game page; Original still requires explicit clean-state confirmation.
- Use 30 px table rows, compact toolbars and review dialogs, and smaller naming forms to display more files at once.
- Arrange Settings in two columns, with storage and artwork controls beside application updates.
- Run launcher and custom-folder scans in the background while the app remains usable; apply scan results after any active file operation finishes.
- Expand managed-file discovery to `OptiScaler.asi`, `ReShade64.dll`, ASI plugins, graphics-library filename families and companion YAML/XML files, with review before capture.

### Fixed

- Fix cross-thread WPF control access when establishing Original after an earlier backup.
- Read approved package conflict/override options on the UI thread before background installation or rollback.

Validation passed on Windows and Linux: 57 service tests on Windows (55 plus two Windows-only skips on Linux/macOS), CLI/menu end-to-end checks, five native CLI updater cases and nine desktop/paired updater cases. Native WPF coverage includes Original creation after an earlier backup during a background scan, a 54-file fixture, filtering and a minimum 340 px table pane. Real gaming-PC acceptance testing remains pending.

## [0.3.0] — 2026-10-30

### Added

- Dark native WPF library cards with automatic keyless game thumbnails. Desktop startup scans installed launchers and queues background thumbnail checks for the entire catalog, not just the visible page.
- Thumbnail matching uses a known Steam app ID when available; otherwise it accepts only a unique, exact normalized game-name match through Steam's public, undocumented storefront search and CDN. Unmatched games retain local artwork or initials.
- Persistent SHA-256-verified JPEG cache under `<home>/thumbnails`, HTTPS-only requests and a 4 MiB image limit. Successful thumbnails are reused for 30 days; no-match results are retried after seven days.
- Thumbnail downloads pause when offline or rate-limited. Settings provides manual refresh and an opt-out; CLI users can set `config set download-thumbnails false`. Downloads are enabled by default.
- Paginated library browsing, search across the full catalog, backup-status filters, and keyboard shortcuts for search, library navigation and scanning.
- Dedicated game workspaces with back navigation, contextual actions and disabled file-changing actions when a game's drive is unavailable.
- Guided file selection and user-confirmed Original backup setup, including already managed and newly detected files with single-click inclusion controls.
- Recovery-backup filter and retained snapshot, package and installation selections after workspace refreshes.
- Desktop storage settings to copy and verify saved payloads before switching backup locations, retaining the original copies.

### Changed

- Apply the dark theme across the main window and review dialogs, with visible errors, empty states and operation progress. Compact game workspaces scroll to preserve readable tables.
- Show startup progress while opening storage and recovering unfinished operations in the background.
- Desktop and CLI target version 0.3.0. Catalog storage remains schema 2, with a backward-compatible thumbnail-download preference.

Validated with 49 passing Windows service tests, CLI/menu end-to-end checks, native updater integration tests and desktop behavior/render checks, including cached thumbnail display/preferences, fresh catalogs, pagination and compact layouts. Linux/macOS pass 47 service tests with two Windows-only skips. A live Steam lookup, HTTPS image download and persistent-cache verification also passed. Real gaming-PC acceptance testing remains pending.

## [0.2.0] — 2026-10-30

### Added

- Native WPF desktop client with a searchable game catalog and batch review of discovered managed files.
- Desktop actions to capture, rename and preview restoration of named snapshots.
- Reviewed ZIP import, package installation and renaming, and layered package rollback from the desktop client.
- Shared catalog, payloads and journal recovery with the CLI; background operations keep lengthy file work off the UI thread.
- CLI snapshot folder output and desktop “Show files” / “Open backups” actions for browsing saved files.

### Changed

- Schema 2 stores each snapshot and recovery snapshot as a readable folder of full file copies. Package payloads remain content-addressed blobs.
- Snapshot files are read-only; restores verify recorded hashes and refuse modified backup payloads.
- Automatically convert schema 1 snapshots on upgrade, retaining `catalog.v1.json` and the original blobs. Full snapshot copies use more disk space than deduplicated blobs.
- Snapshot renaming moves its folder and updates references with crash recovery. Changing the backup directory preserves full snapshot copies and their read-only attributes.

Automated Windows/Linux checks cover schema migration, CLI workflows, native updater recovery and desktop startup/rendering, including an actual 0.1.1 → 0.2.0 executable and catalog upgrade. Real gaming-PC acceptance testing remains pending.

## [0.1.1] — 2026-10-30

### Added

- Partial-name game search for finding entries in the game catalog.
- Automatic discovery of standard managed files, optional extra files and external settings, with evidence for ambiguous candidates and a single batch approval.

### Changed

- Preserve existing managed-file selections when adding discovered files.
- Prevent duplicate managed paths and validate the complete selection before saving any changes.

## [0.1.0] — 2026-10-30

### Added

- Windows CLI with interactive game menus and scriptable commands; shared .NET 10 core for a future desktop UI.
- Installed-game discovery for Steam, Epic, GOG, Xbox, EA, Ubisoft, Battle.net and Amazon Games, plus custom-folder scans and manual entries.
- Reviewed management of OptiScaler, companion mods, graphics libraries and selected game settings, including explicitly approved external files.
- Named snapshots, user-confirmed Original baselines, exact managed-state restoration and recovery snapshots.
- Local ZIP package import with reviewed file mappings, independent version labels, original-file backups and layered installation history.
- Package rollback with overlap-order checks, detection of externally edited files and recovery before explicit overrides.
- Payload integrity checks, guarded destination paths, archive limits and journal-based recovery of interrupted file operations.
- GitHub Releases update checks and confirmed installation for published Windows executables, with checksum validation and executable rollback.
- Configurable backup storage, update checks and archive limits.

CLI 0.1.1 Windows/Linux CI and the release workflow succeeded. Real gaming-PC acceptance testing is still pending, including launcher compatibility and executable replacement against an actual game installation.
