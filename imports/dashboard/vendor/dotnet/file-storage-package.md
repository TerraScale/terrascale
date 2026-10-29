# File storage SDK package

The dashboard now consumes TerraScale.DotNet 0.0.10 from merged ts-dotnet
revision `7a70b21055c3607e3d77fa07fec3ef3ce27e2a8b` (PR61). This supersedes
the previous pin described in README.md; previous package artifacts are removed.

The SDK includes nine typed file-storage operations and file bytes in shared
storage usage. It preserves the authentication branding, account deletion and
project erasure contracts from prior revisions. File-storage snapshots identify
the merged producer `1c94fe262795ea87f55248989e04f5b93f2f40bf` (terrascale PR602).

On beta, `scripts/pack-reproducible.py` built the exact merged revision twice
in isolated directories with locked restores. The package bytes matched, and
installed-package HTTP fake consumers passed on net10.0 and net11.0.
`provenance.json` records package, assembly and all packed contract hashes.
`scripts/verify-sdk.py` validates the package and central pin. This is a vendored
package; HTTP fakes do not establish live service compatibility.
