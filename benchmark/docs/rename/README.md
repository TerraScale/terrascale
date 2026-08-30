# Product-name migration record

The canonical Cargo cutover completed 2026-08-30: the product, root package,
library target, and every workspace dependency key are `TerraBase` /
`terrabase`, and the N-1 dependency-alias window is closed. The retired
spellings and the old benchmark-lane source symbol were removed with the
cutover.

The Python product-name scanner that enforced this migration was removed with
the scripts suite (2026-08-30); its rules live on in
`src/product_identity.rs`, which pins the canonical names, the cutover date,
and the persisted-format compatibility identities in tests. Historical
allowlist entries for wire-compatibility tokens are preserved in git history
and in `docs/design/product-rename-compatibility.md`.

## Identity inventory

These are source identities and are required to stay canonical:

| Surface | Canonical identity | Gate |
| --- | --- | --- |
| Product metadata | `TerraBase` | `src/product_identity.rs` |
| Rust package and library target | `terrabase` | `tests/architecture/product_identity.rs` |
| Workspace dependency keys | `terrabase-*` (and root `terrabase`) | `tests/architecture/product_identity.rs` |
| TBEP protobuf package | `terrabase.v1` | `tests/architecture/product_identity.rs` |
| Go client import path | `terrascale.tech/terrabase-go/terrabase/v1` | `tests/architecture/product_identity.rs` |

The retired `terrabase v2`, `terrabase-v2`, `terrabase_v2`, and `terrabasev2`
spellings are not valid package or dependency identities after the cutover.
`TerraBaseV2` and `TBV2*` remain only where they are persisted-format or
canonical-hash bytes; changing those bytes would make existing data unreadable.
Historical issue references, branch names, and evidence commands are retained
in the allowlist and are not source identities.

The strict Forgejo gate also runs `buf lint`, `buf breaking`, and deterministic
binding regeneration. A schema or generated-binding change must update the
contract and its checked-in output in the same change; a dirty generated tree
fails CI. CI regenerates and compares the permitted `terrabase-go` bindings.
The legacy root Go module has been removed.
