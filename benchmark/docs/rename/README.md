# Product-name migration record

The canonical Cargo cutover completed 2026-08-30: the product, root package,
library target, and every workspace dependency key are `TerraBase` /
`terrabase`, and the N-1 dependency-alias window is closed. The retired
spellings and the old benchmark-lane source symbol were removed with the
cutover.

The Rust architecture gate in `tests/architecture/product_identity.rs` is the
rename scanner. It inventories Git-tracked and non-ignored untracked paths,
reads bounded UTF-8 files, checks filenames as well as contents, and rejects
unregistered retired values. Binary files and the vendored generated Monaco
asset tree are the only content classes skipped. The gate also rejects path
traversal, duplicate or stale registry rows, changed token locations, and
changed token bytes.

`legacy-name-allowlist.tsv` has five columns: path, match count, SHA-256
fingerprint, compatibility class, and reason. Classes separate `persisted`,
`wire`, `historical`, and `policy` occurrences. Persisted and wire bytes are
format contracts; historical and policy entries explain why old text remains.
Adding a row requires reviewing the exact match and its class. The scanner
never approves an absent path or a path whose registered value was removed.

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
`TerraBaseV2` and the supported `TBV2*` values remain only where they are
persisted-format or canonical-hash bytes; changing those bytes would make
existing data unreadable. The removed encrypted bundle codec is not a current
compatibility promise, so its former bundle magic and suffix are not listed.
Historical issue references, branch names, and evidence commands are retained
in the allowlist and are not source identities.

The strict Forgejo gate also runs `buf lint`, `buf breaking`, and deterministic
binding regeneration. A schema or generated-binding change must update the
contract and its checked-in output in the same change; a dirty generated tree
fails CI. CI regenerates and compares the permitted `terrabase-go` bindings.
The legacy root Go module has been removed.
