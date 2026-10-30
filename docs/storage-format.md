# Storage format and recovery

The current storage schema is **1**. Unsupported catalog or journal versions are rejected without conversion. Back up both the catalog home and the configured payload directory; payloads alone do not retain game destinations or history.

## Layout

Default home: `%LOCALAPPDATA%\OptiScalerBackup`. `--home <folder>` selects another home.

| Path | Contents |
| --- | --- |
| `catalog.json` | Schema version, settings, games, approved roots, snapshot manifests, package manifests and installation history |
| `session.lock` | Exclusive process-wide session lock; serializes operations using this home |
| `pending.json` | In-progress game-file transaction journal; exists until commit or recovery finishes |
| `errors.log` | Timestamped CLI/menu errors |
| `backups/blobs/<first-two-hash-characters>/<sha256>` | Immutable, content-addressed file bytes |
| `backups/staging/` | Temporary import/capture content |
| `updates/` | Downloaded releases, updater helper executables and update recovery state |

`Settings.BackupDirectory` overrides the default `backups` payload root. Moving it through the CLI copies/verifies blobs, updates the catalog only after successful copying, and retains the original copies. Snapshot/package content is not automatically pruned. Inspection can cache blobs that do not become an imported package; preserve storage until a future explicit cleanup feature is available.

## Catalog and manifests

IDs are stable GUIDs encoded as 32 hexadecimal characters. Display labels are separate from IDs and filesystem paths. Timestamps are UTC `DateTimeOffset` values serialized as ISO 8601 strings.

Each game records an installation directory, source launchers, executable candidates, selected managed targets, approved external roots, snapshots, imported packages and installation history. Paths use forward slashes in manifests and Windows case-insensitive comparison for matching.

A target has `Root` and `Path`:

- `Root: "game"` resolves beneath the game's installation directory.
- Another root ID resolves beneath that game's explicitly approved `ExternalRoots` directory.
- `Path` is a validated relative file path. Rooted paths, traversal, Windows device names and unsafe characters are rejected. Linked/junction ancestors are rejected at resolution. An unavailable approved root stops resolution; access failures are never captured as historical absence.

A file state records `Target`, `Hash` and `Size`. `Hash: null` means the file must be absent, with size zero. Otherwise the hash is lowercase SHA-256 and locates the blob. Payload content is verified before use. Bytes are immutable; labels may be renamed. Filesystem timestamps, permissions and alternate data streams are not versioned.

A snapshot records its ID/name/time, Original/recovery flags and file states. Original is a one-time user-confirmed designation, not proof of a pristine installation. Restoration uses only the snapshot's recorded targets. A null hash explicitly authorizes removal; paths added to management after capture are preserved and listed as unrecorded in preview. Historical targets remain part of their snapshot even after removal from the current managed selection.

A package records its ID/name/version/time, source ZIP hash, reviewed source-entry-to-destination mappings, excluded entries and destination file states. The original ZIP is not required for reinstallation: extracted payloads are retained as blobs. Excluded ZIP entries are not installed. Imports do not change game files.

An installation records its own ID, package ID, time, rollback time when applicable, installed file states and the complete pre-install snapshot of affected destinations. A later installation becomes a new layer, even for the same package. Active-layer dependencies are derived from overlapping target paths and history order. Newer overlapping layers must be rolled back first. Snapshot restores touching an active package layer are blocked.

Local metadata is trusted application state, not a signed interchange format. Do not edit it while the app is open or import another person's catalog as a way to install a package.

## Game-file transaction recovery

The engine checks destinations, process/file access, payload integrity and available space; it then captures the affected pre-operation state. A flushed, atomically replaced journal is written before destination changes.

`pending.json` contains:

- `SchemaVersion`.
- `Id`: transaction identifier used to clean interrupted per-file staging.
- `BeforeGame`: catalog state to restore on failure.
- `BeforeFiles`: immutable pre-operation file states, including absence.
- `Desired`: intended file states for the attempted operation.

Destination replacements use temporary files beside each destination, verify staged hashes, then replace individual files. This is a journaled multi-file operation, not a filesystem-wide atomic transaction. On success the new catalog is saved and the journal removed. Successful installs/restores/rollbacks retain their generated recovery snapshot in the game history.

A failed operation attempts immediate rollback. If rollback cannot finish, the journal remains. At the next normal launch the app restores `BeforeFiles` and `BeforeGame` before allowing other work. Recovery is repeatable; the game directory must be available and the game stopped. A crash after catalog persistence but before journal removal is conservatively rolled back as an interrupted operation.

Do not delete the journal to bypass recovery. Reconnect the game/backup drive, close processes and resolve the reported access problem, then relaunch. If a blob is missing or corrupt, restore it from an independent copy of the backup store before retrying. Unsupported schema errors require a compatible application version; no destructive migration runs automatically.

## Application update recovery

Update state is separate from game-file transactions. `updates/pending.json` records schema, data home, executable/download paths, old/new hashes, release version, parent process ID and status. The helper waits for the parent to exit, acquires the same session lock and refuses a pending game transaction.

The updater verifies downloaded content against the checksum from the configured GitHub release, retains the previous executable beside it with a `.previous` suffix, stages replacement beside the executable and performs a startup health probe. The helper retains the session lock while the probe validates executable identity/version and reads catalog metadata without acquiring a second lock. Failed replacement/health checks restore the previous executable. Interrupted update state triggers recovery on the next launch. Diagnostic details may be written beside the update journal as `.error`; successful health confirmation uses `.healthy`.

Catalogs and backup payloads are preserved during executable updates. Release checksums provide integrity against accidental corruption; independent publisher signatures and hostile-local-user protection are not provided by this format.
