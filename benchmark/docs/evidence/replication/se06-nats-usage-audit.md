# SE-06 NATS usage audit: no database data rides NATS

This file records the audit artifact of TerraBaseV2's NATS KV/config/
discovery usage boundary (charter PD-2026-08-26-13 D2, ADR 0012 D-2): the
proof that no database data — documents, change events, journal or
replication frames, sync checkpoints, or any other client or member
payload — is stored in or transported through NATS, and that the usage
stays in its own account and subject namespace under the Orchestrator NATS
spec's separation rules.

- Base: `develop @ ef72eca` (branch `fleet/se06-2`, worktree
  `terrabasev2-se06`), crate `crates/terrabase-member-transport`,
  module `src/nats_usage.rs`
- Producing tests: `tests/nats_usage.rs` (written red-first: the module did
  not exist when the tests first failed to resolve it)
- Run command: `cargo test --offline --locked --test nats_usage
  no_database_data -- --nocapture`
- The three allowed roles: operational KV (revisioned puts, compare-and-swap
  ownership transitions), configuration sharing (versioned, monotonic),
  and service/member discoverability (heartbeated records with a
  lease-bounded healthy/stale/missing observation).
- The closed record set: every record the surface can store is one of
  three typed shapes (`Operational`, `Config`, `Discovery`) whose fields
  are validated identifiers, plain numbers, or a socket address. There is
  no byte and no free-text payload parameter anywhere on the surface, so
  database data cannot ride it.
- Subjects: `terrabase.db.<cluster>.{kv,config,discovery}.<key>` — NATS
  subject tokens are validated stricter than identifiers (no whitespace,
  no wildcards, no control characters), so document-shaped text cannot
  even pose as a subject token.
- Cluster separation in the fixture: the database account
  (`terrabase.db.>`) and a distinct control-plane account
  (`terrabase.orchestrator.>`) hold disjoint generated credentials; a
  database-authenticated client cannot address a control-plane subject and
  vice versa (typed `CrossAccountSubject`), and one account's credential
  never authenticates the other.

## Audit artifact (verbatim output)

```text
nats usage audit
check: subject namespace: pass
check: closed value set: pass
check: no raw byte path: pass
check: account separation: pass
claim: none beyond the usage boundary; no database data rides NATS
```

## What the audit proves, and how

1. **subject namespace** — every subject the usage stored parses back into
   a database-namespace role shape; a probe entry under a foreign subject
   flips this check to fail (proven by
   `the_audit_fails_when_a_foreign_subject_or_marker_appears`).
2. **closed value set** — every stored value is one of the three record
   kinds; the enum admits no other variant.
3. **no raw byte path** — every field of every stored record re-validates
   as an identifier, a number, or an address; the smuggling tests feed a
   canary document body and an encoded member envelope into every
   string-ish constructor parameter and observe the typed rejections.
4. **account separation** — the fixture's account credential sets are
   disjoint, and no stored subject falls under another account's prefix.

## The integration fact

Member discoverability through NATS carries the engine-socket lane's
endpoint addresses — connection metadata only. The
`discovery_names_the_engine_socket_lanes_endpoints` test publishes each
lane Member's bound QUIC address as a discovery record and resolves it
back; no member frame, document, or journal content ever appears in any
record (the audit's content scan covers the whole store).

## Limits and unrun

- The audit runs against the in-fixture NATS-facing semantics
  (`NatsFixture`), which models the KV/config/discovery behavior and the
  account/subject separation rules; it is not a NATS server. A live
  `nats-server` run against the same typed surface — with real NATS
  accounts, JWTs/NKeys, and mTLS — is the deployment integration step and
  is recorded here as **unrun** (no `nats-server` binary in this
  environment).
- Lease ticks are fixture ticks (a publish is a heartbeat); they are not
  production timing.
- No production claim: this artifact proves the boundary's contract at the
  named base, nothing about a deployed NATS topology.
