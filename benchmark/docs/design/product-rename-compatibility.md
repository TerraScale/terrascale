# TerraBase rename compatibility

Status: canonical Cargo cutover active. This document records identifiers; it
does not activate a new persisted or protocol format.

## Canonical naming

The product, root Cargo package, library target, and workspace dependency key
are named `TerraBase` and `terrabase`. N-1 external Rust consumers may keep
source compatibility by declaring `terrabase_v2 = { package = "terrabase", ... }`
locally; Cargo then exposes the canonical package under their old dependency
key without retaining an old target in this repository.

## Identifiers that do not change with branding

The following values identify formats already written or hashed. They remain
legacy readers and writers in this stage:

| Identifier | Location | Compatibility reason |
| --- | --- | --- |
| `TerraBaseV2\0` | `terrabase-causal` canonical hash prefix | Changing it changes every causal, CRDT, and repair digest. |
| `TBV2FORMAT` | root storage header | Existing durable engine directories start with it. |
| `TBV2ROOT` and `TBV2MANI` | root storage files | Existing roots and manifests require these reader magics. |
| `TBV2LSM1` | LSM format marker | Existing LSM directories use this activation marker. |
| `TBV2EB1` | encrypted backup bundle | Existing bundles require this reader magic. |
| `TBV2MLJ1` | member transport journal | Existing durable lane journals require this reader magic. |
| `manifest.tbv2m` and `.tbv2b` | backup paths | Existing backup discovery and restore use these names. |

These values cannot become `TB*` replacements through a textual rename. ADR
0031 requires expand, activate, rewrite or expiry, and contract stages for a
new persisted major. Only one incompatible persisted major may retire at once.

## Benchmark receipt compatibility

The decision-grade receipt schema remains `benchmark-receipt/v2`. Its Rust
verdict variants `V2Faster` and `V2Regression` serialize as `v2-faster` and
`v2-regression`. Existing receipts, parsers, and the receipt-discipline gate
depend on those exact values, so this rename does not rewrite them. A future
receipt vocabulary must use a new schema identity and an expand-and-activate
transition; it cannot silently reinterpret version 2 evidence.

Those compatibility values do not name the active engine in prose or source
APIs. New receipt source labels use `TerraBase`, and the internal benchmark
driver type is `TerraBaseLane`.

## New transient and operation identities

New operation IDs, session IDs, probes, lane directories, and test temporary
paths use the `terrabase-` prefix. The former short prefix is not a persisted
format identity and is rejected by the repository rename check. This rule does
not change the format magics, backup suffixes, or historical paths listed
above. Readers continue to treat operation identities as opaque strings, so
the generator cutover does not reject an identity recorded before the rename.

## Identifiers already independent of the old product name

TBEP keeps the `TBEP` FlatBuffers file identifier and its assigned field and
union numbers. Public sync keeps `terrascale.sync.v2.msgpack`, numeric message
tags, and `TSN` and `TSS` native payload magics. LSM segment magics use the
`TBLSM*` family. Usage and deletion checksums use versioned `terrascale.*`
domains. Their meanings are protocol contracts, so the product rename does not
alter them.

## Removal gate

The N-1 source alias may be removed by each external consumer after cutover.
Any legacy persisted identifier may be removed only after candidate-bound
evidence proves that no retained WAL, segment, snapshot, backup, PITR, journal,
or recovery tool needs its reader. Unknown new format majors and required
features continue to fail closed.
