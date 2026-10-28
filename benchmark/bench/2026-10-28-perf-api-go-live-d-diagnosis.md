# Live Go parity run d, 2026-10-28: read-only diagnosis of the database-create timeout

Follow-up to `2026-10-28-perf-api-go-live-d.md` (branch `fix/api-go-parity-live-d`).
This is not a performance result. It accepts no cells, makes no performance claim,
and leaves the original server-over-engine <10% target and its denominator as they were.

Scope: read-only. Diagnosed between 10:35 and 10:43 UTC through `ts-orch netbird ssh`
using the admitted operator tools. No tenant, credential, secret retrieval, mutation, retry or
build was used. Nothing ran on beta. On each host, the scripts reduced the data to
allowlisted fields before returning anything: route templates, status codes, histogram
counts and sums, fixed log templates and timestamps. Deployed revision in every region:
`86c7545afb7f335b954d2331b71121b097541af8`.

## Verdict

| Question | Answer |
|---|---|
| Owned database `api-perf-01m610ttq4n8ew5y4q25aj5sc2` reconciled? | **Unresolved** |
| Server-side timeout reason | **Unknown.** The fra create handler was still running after 29.04 s, when the client closed the request (499). Which step it was waiting on is not observable |
| Local CPU contention as the cause | **Unlikely** (inference). The 29 s were spent inside the fra server, after the request arrived |
| Builder CPU provenance | Not established beyond the existing summary. All builder pids have exited |

## Runtime evidence (facts)

The `http.server.request.duration` histograms are cumulative since process start, and
every region was redeployed at about 10:11 UTC. The run's create started at about 10:28:00.

| Region | Route (template) | Method | Status | Count | Sum (s) | Bucket |
|---|---|---|---|---|---|---|
| gru | `/v1/projects/{project}/databases` | POST | 421 | 1 | 0.00016 | ≤1 s |
| fra | `/v1/projects/{project}/databases` | POST | **499** | **1** | **29.04** | >10 s |
| fra | `/v1/projects/{project}/databases` | POST | 201 | 0 | – | – |
| gru | `/v1/projects/{project}/databases/{database}` | DELETE | 421 | 1 | 0.00024 | ≤1 s |
| fra | `/v1/projects/{project}/databases/{database}` | DELETE | **404** | **1** | 0.0022 | ≤1 s |
| sjc | either route | any | – | 0 | – | – |

gru also shows one unrelated POST 201 (12.0 s), five GETs and two PATCHes on these routes.
This diagnosis does not attribute them to anyone.

| Time (UTC) | Host | Record (fixed template) |
|---|---|---|
| 10:28:29.952 | gru | ts-orchestrator WARN `edge proxy error`, error class canceled |
| 10:28:30.042 | fra | ts-orchestrator WARN `edge proxy error`, error class canceled |
| 10:28:30.580 | gru | ts-orchestrator WARN `edge proxy error`, error class canceled |
| 10:28:33, 10:30:53 | sjc | `agent: session failed` (canceled), SwarmKit agent |
| 10:29:56 | gru | `agent: session failed` (canceled), SwarmKit agent |

| fra point-in-time signal (about 10:40 UTC) | Value |
|---|---|
| `terrabase_catalog_anchor_timeouts_total` | 0 |
| `terrabase_catalog_verified` | 1 |
| `terrabase_quota_escrow_acquisitions_total` | no series (no peer unit request since start) |
| `terrabase_quota_escrow_granted_total` | 1 |
| `terrabase_storage_rejected_total` (busy 503) | 155 at 10:40:50, 162 a few minutes later (rising) |
| fra journal 10:27:45–10:29:15, all units | 1,898 records: 1,896 ts-orch `level=info`, 1 INFO, 1 WARN (above); no TerraBase WARN/ERROR |
| Exact owned name in the fra journal, 10:27:45–10:29:15 and 10:11–10:43 | 0 matches |
| Orphan/cleanup templates in the fra journal since 10:11 (`without a catalog registration`, `Retrying database cleanup`, `Database file audit`) | 0 matches each |

Zero log matches prove nothing. Source review shows the create path never logs the
database name. It logs only database ids, and only on later convergence or audit passes.

## Source facts

- ts-go `v1.OperationTimeout` is 30 s and covers auth, resolution and retries (pinned
  module `07ffb51e1d66`, `v1/client.go`). The runner's 60 s setup deadline did not fire first.
- On the internal listener, a `ProjectManagementRoute` without legacy metadata answers 404
  (`Listeners.cs`), so internal-listener 404s say nothing about existence.
  `GET`/`list` databases require a project-scoped `databases:read` token.
  `HistoryErasureInspectionEndpoint` takes an erasure ULID, not a name.
  `CatalogGateStatusEndpoint` reports only gate state and a whole-catalog digest.
  No supported surface looks up a database by name without project scope.
- `AddDatabaseAsync`: the steps are allowance unit, `PrepareDatabaseAsync` (storage),
  admission, then `RegisterAsync`. An exception, including cancellation, before or during
  registration calls `AbandonDatabaseAsync`.

## Inferences (not proven)

1. One client create was attempted. It reached gru first and got 421 (wrong region), then
   went to fra, the home region. The fra handler held it for about 29 s, until the client's
   30 s operation deadline. The three `edge proxy error ... canceled` records at
   10:28:29.9–10:28:30.6 match a client-side cancellation. The 499 is the server's record of
   a client abort, not a server timeout code.
2. The delay was server-side, not local. Kestrel timed 29.04 s from the request's arrival
   at fra. Local CPU during setup (10:28:00–10:28:33) averaged 45% of one CPU on a 16-CPU
   host, of which builders accounted for 4.1%. That cannot explain the 29 s.
3. The database was probably not registered. Cancellation inside `AddDatabaseAsync` abandons
   storage, and fra, the home region, answered the exact-name DELETE with 404 in 2 ms. But a
   404 can also come from project resolution, and a commit that landed just before
   cancellation would not abandon. **Not proven.**
4. The stalled step (allowance, storage preparation, admission or registration) is unknown.
   Busy-storage rejections were rising on fra at the time. That is a candidate cause, not
   one shown for this request.

## CPU provenance (delta, run-private summary, process names only)

| Window | Samples | Top external (mean % of one CPU) |
|---|---|---|
| Pre-plan | 25 | claude 21.0, node 14.9, dotnet 0.2 |
| Plan + create | 33 | claude 15.6, node 10.4, VBCSCompiler 4.1, dotnet 0.3 |
| Cleanup | 149 | VBCSCompiler 51.5, claude 11.7, node 9.3, dotnet 6.9 |

The builder pids (2 VBCSCompiler, 1 MSBuild, several dotnet) had all exited by
10:42 UTC. Their owner, working directory and start time can no longer be read.

## Minimal missing evidence and safe next step

- To reconcile, one of the following is needed:
  - a `databases:read` token for the owning project, which no longer exists after tenant cleanup;
  - a reviewed operator surface that resolves one exact name to present or absent, without
    listing catalogs. It does not exist and is not invented here.

  Until then, record the name as `reconcile_required` and unresolved.
- To find the cause: a server-side duration or outcome breakdown of the create steps for
  fra. No such metric exists now. A bounded follow-up could add per-step create metrics
  with fixed labels, behind normal review.
- Before any new tenant attempt: a quiet delta (no builders outside slots), plus a fra
  check that `terrabase_storage_rejected_total` is flat over a few minutes. No new tenant is
  authorized by this report.
