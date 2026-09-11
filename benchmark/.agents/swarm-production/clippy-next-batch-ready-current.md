# TerraBase server clippy next-batch readiness

Prepared read-only from `/tmp/terrabase-server-clippy-final-20260911.log`
(247 diagnostics). No clippy/Cargo command was rerun and no product source was
changed while the backup Cargo owner is active. Existing released cohorts and
the currently integrated API, admission, recovery, backup, and broad runtime
paths are excluded.

## Slice A — schema migration mechanics

File: `crates/terrabase-server/src/schema_migration_runtime.rs`

- Replace the immediately dereferenced `&bytes` at line 179 with `bytes`.
- Replace `expected.to_vec()` at lines 358 and 405 with `expected.clone()`;
  these are `Vec<u8>` values and preserve ownership.
- Rewrite the single `serde_json::to_vec` pattern at line 368 as the suggested
  `let ... else`, retaining the existing `Some(collection)` fallback.
- Leave line 410 (`Option` return wrapping) for a semantic review: removing it
  changes the helper's caller contract and is not a mechanical lint fix.

Focused validation: rustfmt/diff checks, then schema migration tests covering
checkpoint persistence, CAS conflict, rollback, and malformed checkpoint
serialization. Cargo execution belongs to the consolidated owner.

## Slice B — configuration and identity mechanics

Files: `config.rs`, `delegated_identity.rs`,
`auth_runtime/byo_config.rs`, `authorization_leases.rs`,
`production_tbep_host.rs`, `reactive_api/socket.rs`.

- `config.rs:1193`: remove explicit `.into_iter()` in the `chain` argument;
  preserve destination ordering and the `current_count` boundary.
- `config.rs:2032`, `delegated_identity.rs:166,183,210,249,330,486`, and
  `runtime`-adjacent unit mappings: use `()` only where the matched success
  type is actually unit; inspect each `Result` type before editing.
- `delegated_identity.rs:150`: use `AuthorizationLeases::default()`;
  no state change. Leave its argument-count and item-order diagnostics for
  API/design review.
- `delegated_identity.rs:475` and `byo_config.rs` item-order diagnostics:
  move local item declarations before executable statements, preserving scope.
- `production_tbep_host.rs:32,60,200`: replace `error.to_owned()` with
  `error.clone()` only when `error` is already `String`; preserve the TBEP
  refusal text.
- `reactive_api/socket.rs:166`: use `clone_from` for the existing destination.
- `authorization_leases.rs:239`: replace `.ok().is_some_and(...)` with the
  direct `Result::is_ok_and(...)` form, preserving the predicate.
- `byo_config.rs:378,380,382`: use checked conversions for parsed key index/
  length values and retain the current configuration-error fallback; this needs
  boundary tests before release.

Focused validation: configuration parsing, delegated identity admission and
lease refresh, TBEP host scope/refusal, socket credential handling, and lease
conformance tests. Do not touch BYOK storage/API architecture in this style
batch.

## Slice C — usage-pool safe mechanics

File: `crates/terrabase-server/src/usage_pool.rs`

- Replace the wildcard import at line 2 with the listed explicit names.
- Introduce a private alias for the complex resolver type at line 7 without
  changing its bounds or ownership.
- Replace lines 139/156 casts with `cast_unsigned`/`cast_signed` only after
  confirming the existing nonnegative/sign bounds; otherwise use checked
  conversion and preserve the current fail-closed error.
- Replace `map(...).unwrap_or_else(...)` at lines 261/271 with the equivalent
  `map_or_else` form.
- Change line 308's by-value argument to a shared slice/reference only if all
  callers already pass borrowed data; preserve allocation and journal order.
- Leave line 165 (`too_many_lines`) for a cohesive billing-flow refactor.

Focused validation: usage-pool allocation, reservation/release, exhaustion,
and journal replay tests; check signed/overflow boundaries explicitly.

## Slice D — replication peer low-risk mechanics

File: `crates/terrabase-server/src/replication_peers.rs`

The dead outer `ReplicationPeers::retention` field has already been removed;
the live worker/session frontier owner remains unchanged. The remaining safe
items are:

- line 581: borrow the `DatabaseOwner` argument if it is only inspected;
- line 594: use `BTreeSet::default()`;
- line 684: remove the immediately dereferenced `&name`;
- leave line-count diagnostics at 717/884/1338 for lifecycle/peer algorithm
  review.

Focused validation: replication frontier, real-worker suspension/rebind, and
begin-drain admission/join tests. Confirm retention acknowledgements remain
the sole engine-frontier advancement path.

## Source identities

Captured after the final log against the current worktree:

```text
06649f4d9f52d994db1d3fc5beb7514d9431a71558dff5c8e715cd24b12e7eb6  crates/terrabase-server/src/api_imports.rs
38927f46540e60c97da6ba968f1b72621cca8fa12508f040b1629f9a3cf25d32  crates/terrabase-server/src/schema_migration_runtime.rs
be507a1798372c7717a76622173c34c795c5b942f080986ab1e30e7818a8e187  crates/terrabase-server/src/config.rs
f57e32a35cc1558b90288d8031d46062043b5aa361f6b689f23387df0695b20a  crates/terrabase-server/src/delegated_identity.rs
9a83d47a0107f85afd6009181398e9a3cd0f10ea4bcc78987ba4c1ba330f539d  crates/terrabase-server/src/auth_runtime/byo_config.rs
f0e1a5ca25b12c2de9f616d17a1d359c16ad55f02fd03c8764c8e10b66181e20  crates/terrabase-server/src/production_tbep_host.rs
e4e245ab37f7f6b858b70f2aa5939f2a76d6dddaa3c0e6d8481077472775f1da  crates/terrabase-server/src/reactive_api/socket.rs
4a37078cc361b982d9e259da9e2b5e45abfe0137b192da3936068e34b00720e1  crates/terrabase-server/src/authorization_leases.rs
dcb57be55ed964298ee62613617336619ecaffb488b4eba7ffbe1b4bd83b7408  crates/terrabase-server/src/usage_pool.rs
124410462483fa7b5c69f1331217096df94c6cae96429f83f867d851756ce741  crates/terrabase-server/src/replication_peers.rs
```

The `api_imports.rs` hash is recorded only as cohort/exclusion evidence; that
file is already an accepted API slice and is not proposed for this batch.
