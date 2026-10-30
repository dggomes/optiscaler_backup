# Core

UI-independent .NET 10 services for game snapshots, reviewed ZIP packages and recoverable file operations. The future desktop UI can call these services directly.

- `Store`: catalog persistence, exclusive session locking and verified content-addressed payloads.
- `Engine`: capture/preview/restore, package install/rollback, overlap ordering and transaction recovery.
- `Packages`: bounded ZIP inspection, destination suggestions and reviewed imports with retained mappings/exclusions.
- `SafePaths`: Windows-safe destinations, explicit approved roots, link rejection and free-space checks.
- `Releases`: stable GitHub release selection and verified downloads; executable replacement belongs to the Windows layer.

A missing snapshot entry is unknown; only an explicit absent file state authorizes removal. Game and external roots must be available before resolving targets. Callers supply process checks and confirmations; services enforce data/path integrity and package rollback rules. See [storage format](../../docs/storage-format.md).
