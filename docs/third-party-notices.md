# Third-party recognition metadata

The bundled component catalog contains metadata derived from [DLSSync-Manifest](https://github.com/xt0n1-t3ch/DLSSync-Manifest), by xt0n1-t3ch and contributors. That project publishes its catalog metadata under Apache License 2.0; a copy is provided in [dlssync-catalog-license.txt](licenses/dlssync-catalog-license.txt). Source commit, source-document digest, detached signature and public key are retained in `component-fingerprints.json`.

Our maintainer-side importer verifies the upstream Ed25519 signature before accepting its metadata. End-user catalog refresh trusts this project's reviewed HTTPS metadata; it does not independently re-fetch or verify upstream DLLs or Authenticode signatures. Source labels must not be read as an installation-safety guarantee.

OptiScaler and OptiPatcher records are independently computed file fingerprints or published GitHub asset digests from their official releases. No upstream application code or DLL/ASI binaries are bundled by this feature. Linked binaries retain their publishers' licenses. The recognition catalog does not grant binary redistribution rights.

DLSS Swapper and OptiScaler Client were researched as references; their GPL application code and MD5-only catalogs are not vendored by this implementation. Unknown variants remain unknown until an independently sourced SHA-256 reference is recorded.
