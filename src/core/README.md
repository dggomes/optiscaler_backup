# Core

UI-independent .NET 10 services for game snapshots, reviewed ZIP packages and recoverable file operations, shared by the CLI and desktop interface.

- `Store`: catalog persistence, exclusive session locking and verified content-addressed payloads.
- `SessionLock`: OS-enforced ownership, a brief closing-session retry, stale-marker reset after acquisition and delete-on-close. Read-only stale markers are repaired only after exclusive access by clearing that marker's `ReadOnly` bit; active handles, ACLs and game/catalog permissions are never bypassed or changed.
- `Engine`: capture/preview/restore, package install/rollback, overlap ordering and transaction recovery.
- `Packages`: bounded ZIP inspection, destination suggestions and reviewed imports with retained mappings/exclusions.
- `SafePaths`: Windows-safe destinations, explicit approved roots, link rejection and free-space checks.
- `Releases`: stable GitHub release selection and verified downloads; executable replacement belongs to the Windows layer.
- `ComponentFingerprints`: bundled/cacheable historical SHA-256/size recognition for captured backup manifests and inventories; reports coverage/ambiguity without executing components or substituting for restore integrity checks. See [component recognition](../../docs/component-recognition.md).
- `GameSettingsReader`: read-only Unreal-style INI observations from managed approved current files or verified captured payloads, retaining provenance/conflicts instead of guessing runtime activation. See [Unreal settings](../../docs/unreal-game-settings.md).
- `CompatibilityPacks` / `UpstreamSources` / manager contracts: immutable display-only compatibility data, reviewed source provenance and future analysis/component/file-plan types. See [manager data format](../../docs/manager-data-format.md); this foundation has no network monitoring, activation, executor or CLI/UI integration.

A missing snapshot entry is unknown; only an explicit absent file state authorizes removal. Game and external roots must be available before resolving targets. Callers supply process checks and confirmations; services enforce data/path integrity and package rollback rules. See [storage format](../../docs/storage-format.md).

Compatibility pack format 1 and its independent `data-v` tags do not migrate or modify schema 2 backup storage.

Fingerprint catalog format 1 is a different metadata document, with a project HTTPS refresh/cache and Apache 2.0 DLSSync-derived metadata attribution. Recognition/settings are development features after stable v0.3.2; backup schema 2 stays unchanged. Inventory-only hashes cannot reconstruct uncaptured component/config bytes, and historical settings never fall back to live files.

If session acquisition fails, resolve the reported holder or data-folder access problem. Never delete `session.lock` while another app/updater is using the data home; an unlocked stale marker needs no manual deletion. Linked lock paths are rejected. See [session ownership](../../docs/storage-format.md#session-ownership-and-stale-markers).
