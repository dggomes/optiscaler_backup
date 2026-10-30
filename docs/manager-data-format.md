# Manager compatibility data foundation

The core contains immutable compatibility-data and future operation-plan contracts. This is a **display-only development foundation**: no CLI/UI compatibility feature, bundled pack, upstream monitor, network refresh, activation, installer or executor is implemented by these contracts. Existing schema 2 game backups remain unchanged; this work does not introduce schema 3.

## Pack format and trust

`CompatibilityPacks.Read` takes raw JSON bytes, a caller-supplied **reviewed expected SHA-256 digest**, the running app version and the current time. The bytes must match that digest before parsing. The expected digest must come from a trusted review process; hashing an unreviewed candidate and immediately passing its hash does not establish approval. No signature or upstream-response retrieval is performed here.

`CompatibilityDataPack` format **1** is independent of the app, catalog and backup formats:

| Field | Meaning |
| --- | --- |
| `FormatVersion` | Must be `1` |
| `Tag` | Independent `data-v` tag with four numeric components, each 1–5 digits, such as `data-v2026.10.30.1`; not an application release tag |
| `MinimumAppVersion` | Numeric .NET version supported by the running app |
| `CreatedUtc` | Pack creation timestamp |
| `Games` | Immutable collection of compatibility game records |

Game records contain a unique `Id`, `Title`, launcher-ID dictionary and observations. Each observation contains `Evidence`, separate `Dlss`/`DlssFg` support states, and `Notes`. Evidence retains `SourceId`, `Url`, `Revision`, `ResponseSha256`, `FetchedUtc`, `ParserSchema` and optional `ComponentVersion`. The response digest identifies reviewed source bytes; the reader validates its shape but does not fetch those bytes.

The reader enforces:

- Nonempty input no larger than **4 MiB**; nesting depth at most **16**; required constructor fields present. Malformed JSON, unknown fields and duplicate property names (including case-only duplicates) are rejected.
- Support states are string enums: `Unknown`, `Supported`, `Unsupported`, `NotApplicable`. Integer values and unknown states are rejected.
- Between **1 and 20,000 games**, **1–16 observations per game**, and at most **16 launcher IDs per game**. IDs/titles and launcher keys/values use printable labels up to 160 characters; duplicate game IDs are rejected case-insensitively. Notes are limited to 16,384 characters.
- Supported format/minimum app version, valid source/hash/parser metadata, and creation/fetch timestamps no earlier than Unix epoch or more than five minutes in the future. Evidence cannot postdate pack creation by more than five minutes; parser schema must be 1–1000.

Returned records use immutable collections. `Read` and matching do not save a pack or change an active data pointer. A rejected candidate leaves activation untouched because activation is not part of this slice.

## Support meaning and matching

Observations preserve their source and notes individually. Released OptiPatcher evidence and preview/main evidence remain separate; conflicting observations are not collapsed into a Boolean or resolved by silently preferring newer/main data. `Unknown` preserves unavailable/unparsed support information; `NotApplicable` preserves N/A. Neither means unsupported, and notes are not interpreted as executable instructions.

Matching first uses exact known launcher IDs (case-insensitive launcher names and values). Multiple candidates are `Ambiguous`. If an ID-matched candidate contradicts another known ID for that game, matching also returns `Ambiguous` rather than choosing an edition from its title.

Without an ID match, title fallback normalizes Unicode, whitespace and case only. It requires a unique exact normalized title and excludes candidates contradicting supplied known IDs. Duplicate titles remain ambiguous; partial/fuzzy titles do not match. Results retain candidate IDs through `Unmatched`, `LauncherId`, `ExactTitle` or `Ambiguous` match kinds.

## Source registry and attribution

`UpstreamSources` records source identities, purposes, reviewed baseline revisions and license/reuse notes. It does not poll them, identify new releases or activate their contents. Validation checks evidence shape, URL and source role, relying on the caller's independently reviewed expected pack digest. It does not resolve or verify a GitHub release-tag-to-commit binding, fetch source responses, establish a real game's capabilities or authorize automatic installation. A later release provider must independently validate the selected release commit before making recommendations; released and main/preview purposes remain separate. Reviewed baselines are recorded references, not a guarantee that arbitrary later evidence belongs to a release.

| Registry ID | Purpose and reviewed source |
| --- | --- |
| `optipatcher-release` | Released compatibility; [GameSupport.md at `5ec46fc7…`](https://github.com/optiscaler/OptiPatcher/blob/5ec46fc7a1ac1aad0ac0aa8dcf71065ce43950fc/GameSupport.md) |
| `optipatcher-main` | Preview compatibility; [GameSupport.md at `c39c29ef…`](https://github.com/optiscaler/OptiPatcher/blob/c39c29efa41e2fa4e6c4e9447fc02eff468a4f8c/GameSupport.md) |
| `optiscaler-wiki` | Mutable official [Compatibility List](https://github.com/optiscaler/OptiScaler/wiki/Compatibility-List); response hashes preserve source identity |
| `optiscaler-client-reference` | Reference only; [Optiscaler-Client at `f24058f6…`](https://github.com/Optiscaler-Client/Optiscaler-Client/tree/f24058f6d0ad7dc9cf88d5fec426a8f8c98fc20f/Services) |
| `opticlick-reference` | Reference only; [Alili100/OptiClick at `5d73cf8d…`](https://github.com/Alili100/OptiClick/tree/5d73cf8de9b83529e34db4fc9f6b52345ad359da/OptiClick.Wpf/Install) |
| `opticlick-historical-reference` | Historical reference only; [red-Geck0/OptiClick at `a9f20bbd…`](https://github.com/red-Geck0/OptiClick/tree/a9f20bbdaead22177fae7dcab5443f7ff1c5db31/assets/data) |

Compatibility evidence must use a registered non-reference source, HTTPS without credentials/nondefault ports, and a URL within that source's repository. OptiPatcher evidence requires a 40-hex-character revision, the exact corresponding `blob/<revision>/GameSupport.md` URL and a nonempty component version up to 80 characters. Wiki evidence must remain within the official OptiScaler wiki. Unknown and reference-only sources cannot supply pack observations.

The reviewed [Optiscaler-Client license](https://github.com/Optiscaler-Client/Optiscaler-Client/blob/f24058f6d0ad7dc9cf88d5fec426a8f8c98fc20f/LICENSE) and [Alili100/OptiClick license](https://github.com/Alili100/OptiClick/blob/5d73cf8de9b83529e34db4fc9f6b52345ad359da/LICENSE) contain GNU GPL version 3. Their inclusion here attributes architectural references; no upstream code or compatibility dataset is vendored by this slice. Specific code/data reuse, redistribution and attribution reviews remain pending before any future bundling. OptiPatcher, wiki and historical OptiClick data likewise retain pending reuse/redistribution review; registry inclusion is not approval to redistribute them.

## Shared future contracts and boundaries

`GameAnalysis` records runtime/executable candidates, preferred GPU identity, evidence and warnings. `ResolvedComponent` describes an already resolved version/channel/asset/hash. `FileOperationPlan` joins those inputs with the data-pack hash, desired files, preconditions and warnings. These types neither resolve downloads nor execute file changes.

Schema 3 recovery, 7z handling/artifact caching, refresh/activation/rollback, upstream parser/provider implementation, a reviewed bundled pack and CLI/UI integration are separate work. Until those capabilities exist, this foundation does not change game files, current snapshot semantics or the application's existing network policy.
