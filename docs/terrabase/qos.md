# Organization quality of service

This implementation is node-local. Resource admission uses the organization ID, shared across its
projects, databases and API keys. It reads the current catalog plan for each request instead of the
plan held by a previously opened database handle. Replication and system metadata have no commercial
plan ceiling, but retain process-wide safety limits.

## Plan policy

| Resource | Hobby | Pro | Enterprise |
| --- | --- | --- | --- |
| Requests/second; burst | 50; 100 | 500; 1,000 | No plan ceiling |
| Concurrent HTTP requests | 4 | 16 | No plan ceiling |
| SQLite scheduling weight | 1 | 4 | 16 |
| Per-organization memory/cache share | 1/16 | 1/4 | Process/cache ceiling |
| Query SQLite execution time | 1 second | 5 seconds | No plan ceiling |
| Query VM instructions | 1 million | 5 million | No plan ceiling |
| Candidate document rows/JSON bytes | 10,000 / 16 MiB | 100,000 / 128 MiB | No plan ceiling |
| Live document JSON storage | 1 GiB | 100 GiB | No plan ceiling |

These are enforcement defaults, not billing prices or a subscription authority. Rate/concurrency and
memory refusals return HTTP 429 with `Retry-After`. Saturation of the global storage admission remains
HTTP 503. Query refusals retain the existing query error contract. Enterprise still observes document,
page, batch, cache and process safety bounds.

The request holds a reservation of 16 KiB plus three times its content length before decoding.
An unknown-length body uses the base reservation for one bounded 4 KiB scratch read, fills it across
network fragments, and reserves three times the actual bytes before returning them to the decoder.
EOF requires no additional reservation. Result buffers reserve estimated JSON and object bytes before
materialization or cache-hit copies; their leases last through response serialization. These are managed
buffer admission estimates, not accounting for every native SQLite or runtime allocation. Transient reservations and retained
cache entries share the organization's memory ceiling: small process budgets can refuse a request with
429 even if its document is within the document-size bound. Each cache partition uses CLOCK second-chance eviction for its own
entries; admission to a full process cache cannot evict another organization's cached data. A plan downgrade
trims excess retained cache entries before request memory admission so the organization can recover.
Authenticated management routes without a resolved organization reserve process memory before decoding;
multi-organization commits attribute their organization work after validating the envelope.
Protected routes establish process admission before authentication and catalog credential/plan reads.
Their result owner remains active through response serialization.
Catalog followers apply organization admission before reading or buffering forwarded uploads; the
primary independently applies its node-local policy. Forwarding preserves the original request path.

## Scheduling and query accounting

SQLite read work uses weighted virtual finish order per organization. Hobby admission leaves capacity
for paid/system work. With at least two storage threads, Hobby calls leave one worker available for
paid/system calls. A single storage thread cannot isolate an already running SQLite call. Scheduling
does not preempt native calls or reserve disk bandwidth; latency isolation requires load validation
on the target hardware. Dedicated per-database write lanes retain their existing lifecycle.
Queued calls borrow only the tenant, result owner and existing engine admission, rather than the caller's
full execution context. Grouped writes retain their batch admission across asynchronous callbacks.
Mutations and index builds observe cancellation between native statements, preserving other writes in
the same group commit. Cancelling a request therefore waits for its current mutation statement to finish.

The scan guard counts candidate stored JSON sizes before evaluating residual document predicates.
Indexes can reduce those candidates. This measures logical candidate rows and JSON bytes, not physical
SQLite page reads, WAL traffic or operating-system I/O. The progress handler separately limits VM work
and SQLite execution time and honors cancellation. Existing sort/page bounds still apply.

## Storage scope

Client document writes serialize through a bounded organization gate. The quota checks net live JSON
growth inside the write's private savepoint before commit, and a refusal rolls that savepoint back.
Deletes and net reducing writes remain allowed when a downgrade puts the organization above quota.
Usage is derived from real registered database contents, with a bounded cache invalidated by database
incarnation and commit sequence. A restart reconstructs it from stored data.

Branch and restore publication hold the same gates from before copying through registration or rollback.
Management commits acquire distinct gate stripes in order and check aggregate staged net growth before
taking the catalog writer. Exact staged contents count, including an older backup larger than its live source.
Enterprise copies bypass commercial quota accounting. Cold accounting uses a trusted engine scalar byte
total without request query deadlines or collection-statistics materialization; cancellation still interrupts
the scan. Accounting-capacity refusals return 503 `storage_accounting_busy` rather than publishing unchecked
growth. Net reducing document writes bypass these scans. Growing writes enumerate local databases;
valid cached totals avoid rescanning stored document sizes.

The ceiling covers registered live client document JSON on this node, including published branches/restores.
SQLite files, indexes, WAL/history, backups and already accepted replica writes are not an atomic quota ledger.
Internal catalog/dashboard document space is system data. Concurrent multi-region acceptance and
replication do not share a distributed reservation, so this implementation cannot
claim a global physical storage quota.

## Metrics

`terrabase.qos.rejected` records the fixed plan/reason labels and `tenant_bucket` from 0 through 63.
`terrabase.qos.request.duration` records admitted HTTP duration by plan and the same bucket. Organization
IDs never become labels. Buckets intentionally aggregate colliding organizations; use the existing
per-organization usage API for exact attribution. Cache, memory and SQLite pool instruments continue
to describe the process-wide ceilings.

## Plan authority

Only the authenticated management service credential can publish an organization resource plan,
through the plan endpoint or a Management commit. The acting user must still be an organization
owner. Public user tokens cannot grant Enterprise admission. The platform owns billing and decides
which resource plan to publish through its SDK; TerraBase enforces that policy without implementing
subscriptions or Stripe behavior.
