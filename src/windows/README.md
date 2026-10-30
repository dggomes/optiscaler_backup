# Windows adapters

Windows-specific integration around the shared core. This project contains no menus or command parsing.

- `Discovery`: launcher installation metadata, custom-folder discovery and catalog merging. Missing or restricted metadata produces warnings; manual registration remains available.
- `Detection`: graphics/mod/config candidates, executable selection candidates, external settings suggestions and running-game checks.
- `AmazonSqlite`: read-only access to Amazon Games' install database through Windows SQLite.
- `Thumbnails`: keyless Steam manifest-ID/title matching, bounded HTTPS JPEG downloads and a hash-verified cache separate from game backups. Unknown IDs send game titles to Steam's public, undocumented storefront search; only a unique exact normalized-title match is accepted. Successful/no-match results are rechecked after 30/seven days, with explicit retry supported.
- `UpdateHost`: staged executable replacement, health probing, previous-executable rollback and interrupted-update recovery.

Registry adapters and updater execution require Windows. The update helper holds the session lock while the health process reads catalog metadata without opening another session. Release fetching/checksum validation belongs to the core; user approval belongs to the CLI.

The thumbnail client can be tested without Windows; `dotnet run --project tests -- --live-thumbnails` optionally exercises actual Steam matching, download and persistent-cache verification. Images are limited to 4 MiB, search responses to 1 MiB and cache metadata to 16 KiB. Endpoint failures remain optional: the desktop coordinator pauses downloads and retains local artwork/initials rather than blocking game operations. UI scheduling and the download opt-out belong to the desktop layer.
