# CLI

Executable entrypoint and user interaction for OptiScaler Backup. No backup or package transaction logic is implemented here.

- `Program.Main`: argument dispatch, data-home/session setup, pending recovery and error reporting.
- `Application.Run`: scriptable commands, structured output, operation previews and explicit confirmations.
- `Application.Menu`: game navigation, managed-file selection, snapshot actions and reviewed ZIP mappings.

Running without arguments opens menus when input is interactive; explicit `menu` also supports piped input. `help` lists commands. Piped menu sessions and other scripted commands skip startup update checks.

Session-lock errors report the data home and, on Windows when discoverable, the active holder's name/PID. Startup retries a closing session briefly, resets unlocked stale metadata only after exclusive access, and removes its marker on close. Stale read-only markers need no manual deletion; only their `ReadOnly` attribute is cleared under exclusive access. Active legacy handles are never bypassed. Close the owner normally or wait for its updater; do not delete an active lock. Other access/ACL errors require fixing the reported data-folder access. `--version` preserves four-component hotfix versions such as `0.3.0.1`.

The game list accepts numbers or case-insensitive partial names. A unique name match opens directly; multiple matches narrow the list; `*` clears the filter. Opening a game detects new standard managed-file candidates, offers optional extra relative/absolute paths, displays the complete proposed batch with proxy evidence and external paths, then applies it with one confirmation. The manual detection action uses the same flow; there are no file-by-file approvals.

`--home` selects a catalog home. `--yes` accepts the final operation confirmation; overlap and externally edited package files still require their specific override flags.

Snapshot creation inventories the entire game folder (regular-file hashes, paths and folders; links excluded). Initial capture copies approved target bytes only. Original additionally includes in-game `Detection.Files` candidates; external settings still require explicit path approval. Later captures compare with Original's inventory, or the earliest available inventory, and include changed/new/deleted files plus prior targets as self-contained versions. Full hashing can be expensive on large games; changed game assets and game-local saves/logs/caches can enlarge backups.

`snapshots create <game-id> <name> --dry-run` returns that inventory/target plan without creating a snapshot. Normal creation saves the plan's selected bytes; the desktop additionally presents a review confirmation. An inventory-backed Original can remove later-tracked additions proven absent, but cannot restore original bytes never copied at baseline. Unrelated paths are preserved. Removing manual selections does not suppress changes/prior targets included by inventory-assisted capture.

Any inferred target beyond the manual selection is listed on stderr and requires capture confirmation. For scripts, review `snapshots create <game-id> Original --original --confirm-clean --dry-run`, then use the same command with `--yes` instead of `--dry-run` to accept the reviewed extras. Neither flag establishes cleanliness; `--confirm-clean` remains the user's assertion.

## Component and Unreal-settings reports — development

These commands are in development after stable v0.3.2:

- `components catalog`: active catalog digest, record count and source evidence.
- `components refresh`: fetch/validate this project's append-only recognition JSON; no component binary downloads or installation.
- `components identify <game-id> <snapshot-id>`: all exact recorded hash/size matches, unknown builds and copied-bytes/inventory-only coverage. Renamed files can match; identification does not reverify payloads or prove runtime enablement.
- `settings <game-id> [snapshot-id]`: current managed/approved INI observations, or verified captured INIs for that snapshot. Historical reports never fall back to live settings.

Snapshot create/list output includes component reports automatically. Settings retain raw scalar/file/section/key/line evidence, layer/confidence hints and conflict warnings. Custom DLSS numeric enums remain unmapped; 100% scale is not a DLAA inference. See [component recognition](../../docs/component-recognition.md) and [Unreal settings](../../docs/unreal-game-settings.md) for limits, primary-source mappings and Apache 2.0 metadata attribution. Backup schema 2 is unchanged.

Build/run instructions and complete workflows are in the [root README](../../README.md).
