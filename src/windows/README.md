# Windows adapters

Windows-specific integration around the shared core. This project contains no menus or command parsing.

- `Discovery`: launcher installation metadata, custom-folder discovery and catalog merging. Missing or restricted metadata produces warnings; manual registration remains available.
- `Detection`: graphics/mod/config candidates, executable selection candidates, external settings suggestions and running-game checks.
- `AmazonSqlite`: read-only access to Amazon Games' install database through Windows SQLite.
- `Thumbnails`: keyless Steam manifest-ID/title matching, bounded HTTPS JPEG downloads and a hash-verified cache separate from game backups. Unknown IDs send game titles to Steam's public, undocumented storefront search; only a unique exact normalized-title match is accepted. Successful/no-match results are rechecked after 30/seven days, with explicit retry supported.
- `UpdateHost`: shared CLI/desktop target discovery, verified same-release preparation, staged replacement, per-component health probes and group rollback/recovery. Recognized installed executables in the initiating app's folder update together; missing components and copies elsewhere are excluded.

Registry adapters and updater execution require Windows. The helper blocks running companions and pending game/rename recovery, waits for the initiating app to exit, then holds the session lock while health processes validate every component without opening another session. The desktop probe also loads WPF runtime/theme resources. Failure/interrupted replacement restores all `.previous` copies; restart preserves the data home. Release fetching/checksum validation belongs to the core; either frontend collects confirmation.

Paired update jobs use independent schema 2 with `Companions`; legacy single-executable schema 1 remains supported. Terminal jobs are archived to `updates/last-update.json` before restart. Shared schema 1 `updates/last-check.json` caches the latest check result; catalog `CheckUpdates`/`LastUpdateCheck` control the common daily schedule.

The thumbnail client can be tested without Windows; `dotnet run --project tests -- --live-thumbnails` optionally exercises actual Steam matching, download and persistent-cache verification. Images are limited to 4 MiB, search responses to 1 MiB and cache metadata to 16 KiB. Endpoint failures remain optional: the desktop coordinator pauses downloads and retains local artwork/initials rather than blocking game operations. UI scheduling and the download opt-out belong to the desktop layer.
