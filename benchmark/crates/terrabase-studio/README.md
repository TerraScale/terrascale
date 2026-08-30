# TerraBase Studio

The operator's web UI for one TerraBase engine location. It opens exactly one
durable location in-process and drives every operation through the `terrabase`
library's public surface — `Engine::open`, `read_snapshot`/`begin`,
`QueryStore`, `IndexStore`, `Engine::tiers()`, the backup and deletion
domains, and the usage meter vocabulary.

It is **not** a customer boundary and **not** a TBEP client. `terrabase-go` is
the only client allowed to talk to TerraBase; the studio is an operator lane
that happens to run inside the process.

## Run it

```sh
cargo run --manifest-path crates/terrabase-studio/Cargo.toml
```

Then open <http://127.0.0.1:8090/>.

| Environment variable    | Default                  | Meaning                          |
| ----------------------- | ------------------------ | -------------------------------- |
| `TERRABASE_STUDIO_DIR`  | `terrabase-studio-data`  | The durable engine location      |
| `TERRABASE_STUDIO_ADDR` | `127.0.0.1:8090`         | The listen address               |

The studio refuses to start if the address is not loopback. There is no
authentication: bind loopback, or do not run it. If a production deployment
boundary is ever needed, that is a separate decision, not a flag here.

The first run creates the location. Load sample documents from the Documents
page if you want something to look at immediately.

S3 backup settings can be saved on `/backups` or supplied through
`TERRABASE_BACKUP_*` variables. Credentials use the standard AWS environment
or workload identity chain and are never saved in Studio. See
[`../../docs/operations/s3-backup-and-restore.md`](../../docs/operations/s3-backup-and-restore.md)
for the complete bucket policy, lifecycle, KMS, and restore procedure.

## Pages

| Page                 | Route        | What it does                                                                                                                                                                                                                |
| -------------------- | ------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Overview             | `/`          | Engine identity and status marker, published sequence, on-disk size, document counts per tenant and collection, the index catalog, and tier state. Pushed over **one** SSE connection every 2 s. Also storage maintenance (both compaction policies, auto-index lifecycle) and the index lifecycle seam. |
| Metrics              | `/metrics`   | The same live facts on their own page. `/metrics/prometheus` exposes the same counters in Prometheus text format for an operator's own scraper — it is loopback-only with the rest of the studio and is not an authenticated public endpoint.               |
| Cluster              | `/cluster`   | Cluster identity and mode rules from `terrabase-cluster`.                                                                                                                                                                   |
| Documents            | `/documents` | Paginated list, point read by id, create/replace through the vendored Monaco JSON editor, typed confirmation for delete and sample replacement, and sample data.                                                            |
| Query                | `/query`     | Build the typed query — condition kinds, ordering, projection, paging — and read the rows next to the plan's index selection record. A TQL text box runs the same seam.                                                     |
| Indexes              | `/indexes`   | Create explicit indexes, run lifecycle actions, inspect staleness and statistics.                                                                                                                                           |
| Tiers                | `/tiers`     | Configure the Storage Tier Mover's policy, pause/resume, dry run (the explain control), and run one pass. Every receipt, including the ordered reasons the policy weighed, is shown.                                        |
| Usage                | `/usage`     | The closed meter catalog and the folded summary of recorded segments.                                                                                                                                                       |
| Replication          | `/replication` | Region catalog, topology view, acknowledgement vocabulary, and the cluster crate's in-process durability model. Names what is *not* exposed without members.                                                               |
| Backups & deletion   | `/backups`   | Configure and test S3, run full or incremental recovery points, verify a remote chain, restore a selected point into a new location, and drive deletion workflows.                                                          |

`/healthz` and `/readyz` answer for a supervisor.

## Every action answers with its typed outcome

No action can fail silently. Each one returns a Datastar event stream that
patches a receipt region on the page, and the receipt is the engine's own typed
value — a commit receipt with its operation identity, sequence, and durability;
a rejection with its variant and retry disposition; an uncertain outcome that
names the identity to resolve; a mover receipt with its decision and reasons; a
recovery receipt with its chain and counts. A refusal is rendered as a refusal,
never as a blank panel.

## Datastar

The UI is server-sent hypertext. Actions answer `text/event-stream` and patch
the page. The vendored official Datastar module applies those events and owns
all action, signal, and DOM-patching behavior. [`src/datastar.rs`](src/datastar.rs)
emits the documented server-side wire format. The only custom browser code is
[`static/studio-monaco.js`](static/studio-monaco.js), which mounts and disposes
Monaco editors while leaving the textarea as the submitted fallback.

The wire uses the current event names, `datastar-patch-elements` (with
`data: selector`, `data: mode`, `data: elements`) and `datastar-patch-signals`;
removal is `data: mode remove`.

Pages use official attributes such as `data-signals`, `data-on:submit`,
`data-on:click`, `data-init`, and `data-text`. Each full page gets a fresh CSP
nonce from the operating system. Datastar uses that nonce for attribute
expressions. The CSP keeps `unsafe-eval` only because Monaco's vendored AMD
loader requires it; scripts can load only from the Studio itself. The Overview
and Metrics pages each open one long-lived `/events/metrics` connection, and
the server pushes a refreshed panel on its own cadence.

## Styling

[Basecoat CSS](https://basecoatui.com) is vendored as a static asset, pulled
from the official npm distribution with `curl` — never hand-written. The exact
version, source URL, upstream commit, and SHA-256 checksums of every vendored
browser asset (Basecoat, Datastar, Monaco, and their licenses) are recorded in
[`static/VENDORED_ASSETS.md`](static/VENDORED_ASSETS.md).

## Honest limits

These are stated on the pages themselves, not hidden.

- **No authentication, loopback only.** The studio is an operator lane. It
  refuses a non-loopback bind at startup.
- **No tenant or collection enumeration.** `Engine::committed_tenants` and the
  storage port's window reads are crate-private, and every query names one
  collection. The studio keeps its own registry of observed names in
  `<TERRABASE_STUDIO_DIR>/studio/state.json` and lets you type any other. It is
  a registry of names to probe, never a copy of data.
- **No durable usage ledger.** `UsageSegmentLedger` is constructed with
  `default()`, holds segments in memory, has no open path, and no aggregate
  read; nothing in the engine's public API attaches one to a location. The
  Usage page's ledger is studio-local and in-memory, and says so.
- **No live replication facts.** Acknowledgement reports, convergence, conflict
  records, member health, and replication receipts are not exposed in-process
  without members (their constructors are crate-private). The Replication page
  lists them under "Not exposed in-process" instead of inventing numbers.
- **No cluster host.** `ClusterHost`, `ClusterMember`, `BackupSet`, and
  `BackupLastRouter` need real member locations on disk; the studio drives one
  engine location only.
- **The backup scheduler lives with Studio.** It runs only while this process
  is alive. Production must compose the same S3 adapter inside the process
  that owns the live `Engine`; a second process must not open the active data
  directory. S3 lifecycle rules, versioning, and object lock own retention.
- **No tier scheduler.** There is no background mover loop and no cancel
  control; `run` executes one bounded pass on the caller's thread. `dry_run`
  is the explain control. A tierless backend answers `UnsupportedBackend`, and
  that typed refusal is shown.
- **Restore does not switch the studio.** A restore writes a brand-new engine
  location; the running studio keeps serving the one it opened. Reopen with
  `TERRABASE_STUDIO_DIR` pointing at the restored directory.

## Layout

```
src/
  main.rs        configuration, the loopback guard, the listener
  routes.rs      the router: pages and Datastar action endpoints
  state.rs       the one engine location plus the sidecar registry
  engine_ops.rs  every engine interaction, public API only
  datastar.rs    the SSE wire format
  metrics.rs     the live snapshot and the panel it renders
  json.rs        DocumentValue <-> JSON, the engine being serde-free
  html.rs        hand-written HTML helpers (no template engine)
  pages/         one module per page
static/          vendored browser assets, with checksums
```

Engine calls run on `spawn_blocking`, never on an executor thread, because the
engine is synchronous and several paths fsync.

## Tests

```sh
cargo test --manifest-path crates/terrabase-studio/Cargo.toml
cargo clippy --manifest-path crates/terrabase-studio/Cargo.toml --all-targets -- -D warnings
cargo fmt --check --manifest-path crates/terrabase-studio/Cargo.toml
```

The suite covers the SSE wire format, pagination and point reads against real
scratch engine locations, the query-form parser, the JSON bridge, the HTML
helpers' escaping and table shape, the sidecar registry's round trip, rendered
markers, and in-memory S3 full, incremental, verification, and restore paths.

## Dependencies

`terrabase` and `terrabase-cluster` by path; `axum` for typed routing, form
extraction, and an SSE body; `tokio` plus `tokio-stream` for the runtime and
the metrics channel; `serde`/`serde_json` for the studio's sidecar file and the
document editor's JSON; `object_store` for S3 signing, transport, and workload
credentials; and `futures-util` for bounded concurrent transfers. No template
engine is used; the HTML helpers are `format!` and an escape function.
