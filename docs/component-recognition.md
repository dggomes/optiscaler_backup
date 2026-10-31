# Historical component recognition

Available from **v0.3.3**. It identifies recorded component versions in backups; it does not install components, execute downloaded libraries, recommend a compatible combination or prove that a component was enabled at runtime. Backup schema 2 is unchanged. This fingerprint catalog is separate from the display-only manager compatibility pack described in [manager data format](manager-data-format.md).

## Matching and coverage

The application embeds `src/core/component-fingerprints.json`, independent format **1**, so recognition works offline. The initial catalog contains **98 provenance records**: 37 FSR records, fingerprints derived from 29 available official OptiScaler archive assets, and 32 official OptiPatcher ASI assets. Counts describe the initial import, not a promise that every historical/custom build is covered.

Recognition matches the recorded **SHA-256 and byte length**, regardless of the recorded filename. A renamed OptiScaler proxy can therefore match the same bytes. Filenames alone never establish a version. Every matching provenance/version record is retained when identical bytes occur in several releases; the report does not choose an arbitrary winner. Recognizable component filenames without an exact match remain unknown/custom builds; an unfamiliar renamed file cannot be classified from its name alone.

Each report identifies its catalog digest, coverage and warnings. File coverage distinguishes:

- **Saved bytes**: the snapshot manifest references copied payload bytes. Recognition uses the manifest's captured hash/size; it does **not** rehash that payload as part of identification.
- **Inventory only**: the snapshot's game inventory recorded a hash/size, but those original bytes were not necessarily copied. Such entries can establish recorded identity, not restorability.

Missing/unavailable inventories produce a warning and recorded-files-only coverage. No current game files are consulted to fill gaps in an old snapshot. Recognition is not a restore integrity check: edited payloads can still display their original captured identity, while restoration separately verifies the actual stored bytes and refuses mismatches. Presence of FSR, OptiScaler or OptiPatcher files is not evidence of an active runtime renderer/plugin.

## Provenance and metadata licensing

FSR metadata is derived from [DLSSync-Manifest at pinned revision `4f5c6a25…`](https://github.com/xt0n1-t3ch/DLSSync-Manifest/blob/4f5c6a255a4ed6d0b1e92d3e6997fef94abee851/manifest-v3.json). The maintainer builder downloads the manifest and detached signature at the **same commit** and verifies Ed25519 against its pinned public key before accepting SHA-256 records. MD5-only upstream entries are not promoted into SHA-256 evidence.

Full build strings and component roles remain distinct. For example, the initial upscaler records include FSR `4.1.0.0`, `4.1.1.2740` and `4.1.2.4555`; frame-generation/Vulkan/other library roles retain their own records. A marketing version label is not used as a substitute for exact bytes.

OptiScaler and OptiPatcher fingerprints come from their [official OptiScaler releases](https://github.com/optiscaler/OptiScaler/releases) and [official OptiPatcher releases](https://github.com/optiscaler/OptiPatcher/releases). Records retain release/channel, asset ID/URL, archive digest and entry path. Generation checks asset size and GitHub SHA-256 where supplied, hashes archive/file bytes itself, and inspects bounded archives without loading their DLLs/ASIs. Historical records remain even when upstream assets later disappear. Downloaded binaries are not executed, installed or distributed with this metadata.

**Third-party metadata notice:** DLSSync-Manifest metadata, copyright 2026 xt0n1, is derived under Apache License 2.0. See the retained [license text](licenses/dlssync-catalog-license.txt) and [pinned upstream license](https://github.com/xt0n1-t3ch/DLSSync-Manifest/blob/4f5c6a255a4ed6d0b1e92d3e6997fef94abee851/LICENSE). This project adds provenance fields and independently computed official-release fingerprints. No OptiScaler Client/OptiClick GPL implementation or compatibility dataset is copied into this feature. Metadata attribution does not grant rights to redistribute the referenced binary assets.

## Format, refresh and trust boundaries

The JSON document contains `Version`, `Records` and `Sources`. A record retains component/role/full version, original filename, hash/size, source/revision, release tag, asset identity/URL, archive hash/entry and channel. DLSSync source evidence retains the pinned revision/URL, source digest, signature and public key.

The application bounds catalog input to **32 MiB**, depth 12, at most 100,000 records and 10,000 source records. It rejects unknown formats/fields and invalid component/provenance shapes. Component file sizes are bounded to 256 MiB. Recognition does not fetch binary assets.

End-user refresh downloads only this project's reviewed [catalog JSON](https://raw.githubusercontent.com/dggomes/optiscaler_backup/main/src/core/component-fingerprints.json) over HTTPS into `<home>/components/fingerprints.json`. This trusts the project repository's metadata distribution; the application validates the recorded upstream signature evidence's shape, while cryptographic verification of the original DLSSync response occurs in the maintainer builder. It does not independently refetch/reverify every upstream artifact at runtime. Catalog matching does not authorize execution or installation.

Refresh is append-only: a candidate dropping an existing record is rejected before replacing the cache. Invalid/unsupported cache formats are preserved rather than silently rewritten; loading falls back to the embedded catalog when a cache cannot be used. Offline/refresh errors do not block backup operations. The cache lives outside backup payload storage and is not moved by backup relocation.

Desktop startup refresh follows the shared `CheckUpdates` opt-out. After validating existing catalog/check formats, refresh records an attempt in `<home>/components/checked.json` **before HTTP**, so failures also honor the daily automatic cooldown. This bounded check document has independent `Version: 1` and `CheckedUtc`; unknown/future formats are preserved rather than overwritten. Failed requests retain usable catalog data. Settings' separate **Historical component recognition → Refresh recognition catalog** control and CLI `components refresh` perform an explicit retry without waiting for the startup cooldown. No API key or user-supplied web-service URL is required, and local game names/hashes are not uploaded by this refresh.

## Commands and desktop presentation

```powershell
.\optibackup.exe components catalog
.\optibackup.exe components refresh
$gameId = Read-Host 'Catalog game ID'
$snapshotId = Read-Host 'Snapshot ID'
.\optibackup.exe components identify $gameId $snapshotId
.\optibackup.exe snapshots list $gameId --json
```

Snapshot create/list output includes component reports automatically. Desktop **Backups** adds a **Components / versions** column and **Components / settings…** details with every match, source/release and saved-bytes/inventory-only coverage. Results remain observational and do not change snapshots.

## Maintaining historical coverage

`scripts/update-component-catalog.py` appends new source records and archive-derived fingerprints, retaining prior history and writing the JSON only after successful generation. It needs Python 3, Node.js for signature verification, authenticated GitHub CLI access for public release enumeration, and a trusted `7z`/`7zz` archive reader. No end-user API keys are part of the application flow.

```sh
PATH="/usr/bin:$PATH" python3 scripts/update-component-catalog.py
```

Maintainer downloads are cached under `.context/component-history-cache` by default. They are generation inputs, not application payloads. Review generated metadata and attribution before merging. New versions can expand coverage, while absent/ambiguous/custom builds must remain explicit.

[The maintenance workflow](../.github/workflows/component-catalog.yml) collects daily and updates the data-only `automation/component-fingerprint-history` review branch with fast-forward commits, never force-pushing. Pending unreviewed observations are fed back into subsequent collection, so a rolling upstream replacement can be retained even while human review is still pending. `main` is untouched until a maintainer reviews and merges the proposed metadata.

The workflow requests a pull request when repository policy permits. The repository currently disables bot-created PRs (`can_approve_pull_request_reviews=false`), so failure to create one falls back to creating/updating a review issue with the branch comparison link. Approval settings are not changed; there is no automatic merge or unattended publication.

The maintainer job runs service/CLI checks against current code and proposed metadata itself. Bot-authored pushes do not trigger ordinary CI automatically; run/review Windows CI before merging rather than treating the review branch as fully verified. The workflow neither installs nor publishes binary components.
