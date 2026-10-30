# Changelog

## [Unreleased]

### Added

- Native WPF desktop client with a searchable game catalog and batch review of discovered managed files.
- Desktop actions to capture, rename and preview restoration of named snapshots.
- Reviewed ZIP import, package installation and renaming, and layered package rollback from the desktop client.
- Shared catalog, payloads and journal recovery with the CLI; background operations keep lengthy file work off the UI thread.

The desktop client has not been released. Windows runtime smoke testing is pending.

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
