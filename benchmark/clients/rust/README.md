# Rust bench-client (TS-761, Slice 3)

**Blocked by official SDK APIs; this is a contract preflight, not a working
load generator.** The CLI accepts the shared `run` arguments, validates the
workload, then exits 1 with `SDK_BLOCKED`. It does not read credentials, contact
services, create a database, open the output file, or emit benchmark results.

```sh
nice -n 19 cargo build --locked --manifest-path clients/rust/Cargo.toml
```

The official `terrascale` crate is pinned to ts-rust `main` commit
[`ded5adb3b63ce4af0e3a26f95139968cdf539501`](https://github.com/TerraScale/ts-rust/commit/ded5adb3b63ce4af0e3a26f95139968cdf539501),
version 0.1.0. It is a dev-dependency for SDK contract tests; the validation-only
production binary does not compile or call the SDK. Cargo dependency resolution
fetches the public repository from GitHub over HTTPS without credentials. A complete load client
will need the SDK as a production dependency once its APIs support the contract.
The operating contract requires automatic first-party credential creation and
delivery; no hand-copying step is approved. Delivery and the load client remain
unimplemented here. Existing credential inputs are implementation inventory.
The current preflight's closed region selector is also inventoried for removal;
automatic eligible-region discovery/local inference is the target contract.

## SDK follow-ups

| Contract need | Public SDK at the pinned revision | Required follow-up |
| --- | --- | --- |
| Plain ULID IDs | `v1::DocumentId` aliases `uuid::Uuid`; `Client::for_project` also requires a UUID. CRUD and transaction IDs cannot accept plain ULIDs. | Expose the producer's plain ULID identity contract throughout project, document and transaction APIs. Do not encode ULIDs as UUIDs. |
| Create `bench-<run-id>` and delete it at the end using JWT | `v1::Database::create` uses pre-TS-589 `PUT`; `Database` has no public delete. `ControlClient::delete_database` belongs to the legacy `/v1/dashboard` contract and is unavailable for project clients. | Add the current JWT project-scoped database lifecycle, including cleanup after setup/run failures and uncertain creates. |
| `query.indexed` on its throwaway dataset | Project `create_collection` only takes a name. Index configuration exists only on the legacy control client. | Add public JWT project-scoped index provisioning and readiness so the benchmark can establish that an index serves the measured query. |

Available APIs are **not** follow-ups: `auth_idp(...).oidc().token_client_credentials`
supports confidential Basic/form credentials with explicit resource and scope;
`v1::Client::for_project` verifies Atlas session routing; public document
get/put/patch/delete, filtered queries and `Database::commit(Tx)` exist. SDK
`regional_host` exposes a closed public-region helper; it does not implement
automatic discovery of every serving region. These APIs have not
been live-qualified by this slice. Auth instance, resource, scopes and project
binding must be supplied by the dedicated benchmark client provisioning.

Run preflight before fetching or creating benchmark credentials. Safe cleanup
and plain ULID support block every workload; index support additionally blocks
`query.indexed`. No raw HTTP, legacy bootstrap keys, UUID conversion or private
SDK transport calls substitute for missing APIs.

## Checks and evidence

```sh
nice -n 19 cargo fmt --manifest-path clients/rust/Cargo.toml -- --check
nice -n 19 cargo clippy --locked --manifest-path clients/rust/Cargo.toml --all-targets -- -D warnings
nice -n 19 cargo test --locked --manifest-path clients/rust/Cargo.toml
```

Workload validation follows Slice 1's draft protocol at `e8c22b29` (PR #13):
integer percentage weights totaling 100, distinct ops, bounded data/targets,
and explicit increasing ramp steps. The 15-minute whole-run deadline remains
part of the unimplemented load generator.

Tests cover workload fields/operations, protocol bounds, ramp steps, argument validation,
the SDK's ULID rejection, failure without output creation/overwrite, and
credential-safe diagnostics. [GRU preflight evidence](bench/gru-preflight.md)
records the blocked invocation; it is not a live throughput or latency result.

The scheduler, HDR histogram, intended-start correction, per-second JSONL,
summary and ramp are still unimplemented. Implement and test them when the SDK
follow-ups unblock a complete run. Rebase onto Slice 1's `benchmark/docs/protocol.md` and
shared workloads when that slice merges; no shared contract is changed here.
