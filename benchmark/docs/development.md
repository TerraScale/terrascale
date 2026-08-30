# Development contract

The workspace [GOAL.md](../../GOAL.md) is the product and architecture
authority. This repository's [GOAL.md](../GOAL.md) is the execution plan and
current evidence index; the [ADR index](adr/README.md) links its durable
decisions. Repository ADRs record decisions that are hard to reverse; a
`Proposed` ADR is not an accepted decision. Source, tests, and receipts
describe implementation status and must retain their honest limits.

The repository is pre-production. A focused green test or a deterministic
loopback receipt does not qualify OCI placement, WAN behavior, production
latency, compliance, or General Availability.

Do not use RavenDB or TerraBase V1 contracts, citations, parity rows, import
formats, or behavior as V2 acceptance criteria. Native schema migrations,
Partition movement, snapshots, backup, and restore remain part of V2.

Run focused checks while editing:

```bash
cargo fmt --all -- --check
cargo check --workspace --locked
cargo test --workspace --locked
cargo clippy --workspace --all-targets --all-features --locked -- -D warnings
cargo doc --workspace --no-deps --locked
python3 scripts/check_license.py
./scripts/check_secrets.sh
```

The development check records the unresolved distribution decision without
presenting it as release evidence. Candidate admission must also run
`python3 scripts/check_license.py --release`; that command stays red while the
manifest or `LICENSE.md` contains a pending marker. Release mode parses every
source `Cargo.toml`, including excluded tools and private crates omitted by
`cargo-deny`, and follows declared workspace globs, path dependencies, patches,
and replacements even below normally skipped build or dependency directories.
Repository Cargo path overrides and symlinked evidence are refused. Release
inventory also refuses explicit `package.workspace` roots and inherited
`workspace = true` dependencies until the checker can reproduce Cargo's owning
workspace resolution without accepting hidden or external packages. Release
mode requires one exact package inventory, terms digest, legal entity,
versioned proprietary identifier, authorized use and distribution rights, and
approval evidence in `docs/legal/terrabase-license-disposition.json`. The
approval binds the scope, terms version, package inventory, and exact license
bytes. Main-branch CI also requires its SHA-256 through the protected
`TERRABASE_TRUSTED_APPROVAL_SHA256` secret, so repository-controlled files
cannot approve themselves. Legal owners must provide the terms, disposition,
reviewer identities, approval bytes, and protected digest; the checker does not
create or infer them.

If approved repository evidence is present, the development command validates
its structure and exact internal bindings but reports
`approved-metadata-untrusted` with `releaseQualified` set to `false`. It does
not consume or authenticate the protected approval digest. Only `--release`
may report `approved-for-release`, and only after matching that digest in the
protected environment.

CI owns the full `scripts/check.sh` gate. Storage, durability, replication,
compaction, backup, and recovery changes also require the exclusive recovery
lane and a secret-safe Recovery Receipt. Performance claims require a valid
TerraBase.Benchmark receipt.

Protocol or generated-binding changes must run the schema-owner regeneration
and drift checks, plus the shared valid/error and hostile-input vectors. Keep
the schema and generated artifacts in their owning lane; do not hand-edit
generated output. Changes to a release invariant require a receipt naming the
source commit, exact command, environment, result, and scope limits.

Before describing a lane as production-ready, update
[the qualification matrix](evidence/qualification-gap-sweep.md), link the
new receipt, and recheck its status against the current checkout. Do not
silently upgrade `partial`, `missing`, or `blocked-external` evidence.
