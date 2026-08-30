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
