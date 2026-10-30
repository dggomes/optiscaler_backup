# Storage format and recovery

The current catalog/snapshot schema is **2**. Schema 1 catalogs are upgraded automatically while the session lock is held; other catalog versions are rejected. The upgrade is one-way: v0.1.x cannot read schema 2. Back up both the catalog home and configured payload directory; file copies alone do not retain game destinations or installation history.

## Layout

Default home: `%LOCALAPPDATA%\OptiScalerBackup`. `--home <folder>` selects another home. The default payload root is `<home>/backups`; `Settings.BackupDirectory` may override it.

| Path beneath home | Contents |
| --- | --- |
| `catalog.json` | Schema version, settings, games, approved roots, snapshot/package manifests and installation history |
| `catalog.v1.json` | Original schema 1 catalog retained during conversion |
| `session.lock` | Exclusive process-wide session lock |
| `pending.json` | Game-file transaction journal retained until commit/recovery finishes |
| `rename-pending.json` | Snapshot-folder rename journal retained until catalog commit/recovery finishes |
| `errors.log` | Timestamped CLI/menu errors |
| `updates/` | Downloaded releases, helper executables and application-update state |
| `thumbnails/` | Optional downloaded artwork and thumbnail-cache metadata; separate from payload storage |

Paths beneath the configured payload root:

```text
snapshots/
  <Game name> (<game-ID prefix>)/
    <yyyy-MM-dd HH-mm-ss> <Snapshot name> (<snapshot-ID prefix>)/
      game/<game-relative file path>
      <External root folder name> (<root-ID prefix>)/<file path>
blobs/<first-two-hash-characters>/<sha256>
staging/
```

Game, snapshot and external-root name portions are sanitized for Windows and capped at 40 characters. ID prefixes are eight characters, keeping similarly named items distinct. Folder timestamps use local time for readability; manifest creation timestamps remain UTC.

Every named snapshot, package pre-install backup and recovery snapshot has its own ordinary folder of **full file copies**. Snapshot bytes are not deduplicated, even when their hashes match another snapshot. Package ZIP payloads remain content-addressed blobs. Old blobs are retained after conversion.

Snapshot files are marked read-only. This discourages accidental editing; it is not a security boundary. Do not edit them. Every restoration verifies the stored hashes before changing game files and rejects modified/missing payloads. Absent files have manifest entries but no physical file copy. Snapshots with no present files still have a folder.

Changing the backup location through the CLI copies and verifies referenced snapshot files and blobs, preserves read-only snapshot attributes, then switches the catalog. Old copies remain at the previous location. Snapshot/package content is not automatically pruned; inspection can cache blobs that never become an imported package.

## Catalog and manifests

IDs are stable GUIDs encoded as 32 hexadecimal characters. Display labels are separate from IDs. A snapshot rename also renames its readable folder; labels do not become unchecked paths.

Each game records its installation directory, launchers, executable candidates, selected managed targets, approved external roots, snapshots, packages and installation history. Paths use forward slashes in manifests and Windows case-insensitive comparison for matching.

A target has `Root` and `Path`:

- `Root: "game"` resolves beneath the game's installation directory.
- Another root ID resolves beneath that game's explicitly approved `ExternalRoots` directory.
- `Path` is a validated relative file path. Rooted paths, traversal, Windows device names and unsafe characters are rejected. Linked/junction ancestors are rejected. An unavailable approved root stops resolution; access failures are never captured as absence.

A file state records `Target`, `Hash`, `Size` and optional `Payload`:

- `Hash: null` explicitly records absence, with size zero and no payload file.
- A non-null `Payload` locates a snapshot file relative to the payload root. It is validated to stay within that root; its hash and size remain in the manifest.
- A non-null hash with no `Payload` resolves through `blobs`, as used by packages and legacy transaction data.

A snapshot additionally records `Folder`, its readable folder relative to the payload root, plus ID/name/time and Original/recovery flags. Filesystem timestamps, original permissions and alternate data streams are not versioned; read-only attributes protect stored copies rather than defining restored game-file permissions.

Original is a one-time user-confirmed designation, not proof of a pristine installation. Restoration uses only recorded targets. Explicit null hashes authorize removals; paths added to management after capture are preserved and listed as unrecorded in preview. Historical targets remain part of their snapshot after removal from the current managed selection.

A package records ID/name/version/time, source ZIP hash, reviewed entry-to-destination mappings, excluded entries and destination file states. Extracted blobs permit reinstallation without the ZIP. Import does not change game files.

An installation records ID, package ID, install/rollback times, installed file states and the pre-install snapshot of affected destinations. That snapshot can also appear in the game's snapshot list; both copies reference the same snapshot folder. Active-layer dependencies derive from overlapping physical paths and history order. Roll back newer overlapping layers first. Snapshot restoration touching an active package layer is blocked.

Local metadata is trusted application state, not a signed interchange format. Do not hand-edit it while the app is open or use another person's catalog as an installation package.

## Optional thumbnail cache

Catalog schema 2 includes an optional `Settings.DownloadThumbnails` Boolean, defaulting to `true` when absent. This preference does not change snapshot/package formats or require a catalog migration. It controls desktop matching/download requests; previously cached images remain usable when disabled.

`<home>/thumbnails` is independent of `Settings.BackupDirectory`. Backup relocation does not copy/move it, and thumbnail data is not required to restore games. Entries use a SHA-256 key derived from the catalog game ID, name and installation directory:

- `<key>.jpg`: downloaded JPEG, limited to 4 MiB and checked against its recorded SHA-256 before use.
- `<key>.json`: independent thumbnail-cache schema **1**, with `Version`, UTC `Checked`, optional Steam `AppId` and optional image `Hash`. Metadata is bounded to 16 KiB; unknown cache versions are rejected without rewriting them.

Successful entries are rechecked after 30 days; no-match entries after seven days. Explicit **Refresh thumbnails** bypasses those freshness windows. Image replacement and metadata writes use temporary files; unavailable images retain local-cover/icon/initial fallbacks. Corrupt cached image bytes are rejected and may be downloaded again. Old valid images can still be displayed offline.

The desktop attempts all catalog entries in the background, using an installed Steam ID or a unique exact normalized-title match. Unknown IDs send game names to Steam's public, undocumented search endpoint; image requests use HTTPS Steam CDN URLs. Requests need no API key. Search responses are limited to 1 MiB. Offline/rate-limit failures pause image requests rather than backup operations. Application updates and optional thumbnail matching/downloads are the app's network uses; snapshot/package operations remain local.

## Automatic schema 1 conversion

On opening a schema 1 catalog:

1. Retain the original catalog as `catalog.v1.json`.
2. Collect distinct snapshots across the game list and installation pre-state records; check space for their full copies.
3. Verify source blobs, copy each snapshot through staging, mark copied files read-only, then publish its complete folder. An already completed matching folder can be reused after interruption.
4. Update every copy of each snapshot's `Folder`/`Payload` metadata and atomically save the schema 2 catalog.

The same snapshot ID referenced in multiple records is converted once. Any failure leaves the original `catalog.json` unchanged as schema 1 and refuses to open the store with the reported reason. Completed folders from an interrupted attempt may remain for reuse; blobs are never removed by conversion. Allow enough space for all converted snapshots in addition to existing blobs.

There is no automatic downgrade. `catalog.v1.json` preserves pre-upgrade metadata; it does not represent later schema 2 snapshots or operations. Keep an independent copy of the entire pre-upgrade data set if a historical rollback of the application/data is required.

## Capture, rename and operation recovery

Capture checks total required space, copies into a staging folder, hashes the copies and marks them read-only. Only a completed snapshot directory is moved to its final browsable location; a partially captured snapshot is not published there.

A snapshot rename writes `rename-pending.json` before moving the folder. It rewrites payload prefixes and updates both the game snapshot and any matching installation pre-state record before saving the catalog. Failed or interrupted renames roll back through the journal. Do not manually rename snapshot directories in Explorer: use the application so manifest paths stay synchronized.

Game-file mutations check destinations, running processes, file access, payload integrity and available space, then capture the affected pre-state. `pending.json` is flushed and atomically written before destination changes. It records the schema, transaction ID, `BeforeGame`, `BeforeFiles` and intended `Desired` states.

Destination replacement uses verified temporary files beside each destination, followed by individual replacements. This is a journaled multi-file operation, not a filesystem-wide atomic transaction. On success the new catalog is saved and the journal removed. Successful operations retain their generated recovery snapshot. If an operation fails and immediate recovery succeeds, its uncommitted recovery folder is removed.

If recovery cannot finish, the journal remains. The next normal launch restores the pre-operation files/catalog before allowing further work. Recovery is repeatable; relevant game/settings/backup roots must be available and the game stopped. A crash after catalog persistence but before journal removal is conservatively rolled back.

Schema 1 game-file journals remain recoverable after conversion: old bytes still resolve through retained blobs, while snapshots in recovered `BeforeGame` metadata are normalized to schema 2 so the catalog keeps their folder references.

Do not delete journals to bypass recovery. Reconnect drives, close processes and resolve reported access problems, then relaunch. Restore corrupt/missing snapshot files or blobs from an independent copy before retrying. Unknown schema versions require a compatible application version.

## Application update recovery

CLI update jobs use their own **job schema 1**, independent of the catalog schema. `updates/pending.json` records data home, executable/download paths, old/new hashes, release version, parent process ID and status. The helper waits for the parent to exit, acquires the session lock and refuses a pending game transaction.

The updater checks downloaded content against the configured GitHub release checksum, retains the executable beside it with a `.previous` suffix, stages replacement and probes startup health. The helper keeps the lock while the probe validates executable identity/version and reads supported catalog metadata without acquiring a second session. Normal application startup performs any required catalog conversion.

Failed replacement/health checks restore the previous executable. Interrupted updater state triggers recovery on the next launch. Diagnostics may be written as `.error` beside the update journal; successful health confirmation uses `.healthy`. The desktop interface opens release downloads manually; automatic updater behavior applies to the CLI.

Application updates preserve user payloads, but a subsequent one-way catalog upgrade prevents older executables from opening the upgraded data. Release checksums detect accidental corruption; independent publisher signatures and hostile-local-user protection are not provided by this format.

## Session lock startup recovery

`session.lock` is an OS-held exclusive session handle, not an existence flag. Startup resets stale bytes only after acquiring that handle and uses delete-on-close. A read-only stale marker is recovered after an exclusive read handle proves no older session holds it; only this app-owned marker's ReadOnly attribute is cleared. Linked marker paths are rejected. Live sessions cannot be bypassed; Windows Restart Manager provides optional holder name/PID diagnostics without stopping processes. Failed startup releases its handle. Catalog/game data, recovery journals and ACLs are untouched by lock cleanup.
