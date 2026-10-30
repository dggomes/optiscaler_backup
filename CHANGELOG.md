# Changelog

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
