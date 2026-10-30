# Windows adapters

Windows-specific integration around the shared core. This project contains no menus or command parsing.

- `Discovery`: launcher installation metadata, custom-folder discovery and catalog merging. Missing or restricted metadata produces warnings; manual registration remains available.
- `Detection`: graphics/mod/config candidates, executable selection candidates, external settings suggestions and running-game checks.
- `AmazonSqlite`: read-only access to Amazon Games' install database through Windows SQLite.
- `UpdateHost`: staged executable replacement, health probing, previous-executable rollback and interrupted-update recovery.

Registry adapters and updater execution require Windows. The update helper holds the session lock while the health process reads catalog metadata without opening another session. Release fetching/checksum validation belongs to the core; user approval belongs to the CLI.
