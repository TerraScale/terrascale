# Database create stage metrics

`terrabase.database_create.stage_duration` (Prometheus
`terrabase_database_create_stage_duration_seconds`) is a histogram, in seconds, on the
`TerraScale.TerraBase` meter. It records how long each stage of one database creation
took and how that stage ended. Every creation that goes through the management service's
shared create path records it: management and project-API creates, branches, copies
(including from backup snapshots), provisioning, platform seeding and replica materialization. The
`/v1/management/commits` batch path is not timed.

It was added after a live create was canceled at its 30-second client deadline with
no record of which stage held it. It records future creates only. It does not
explain that past failure.

## Labels

Two labels, each with a fixed value domain. No identifiers, names, regions, routes or
error text are ever labels.

| `stage` | What it times |
|---|---|
| `serving` | Reading the serving regions from escrow peers (only creates that spend an allowance unit) |
| `precheck` | The allowance and name precheck read, including any unit acquisition from peers it needs |
| `quota_enter` | Entering the organization's storage quota group (copies only) |
| `replicas` | Choosing default replica regions (only when the request names none) |
| `prepare` | Recording the cleanup intent and creating or copying the database file |
| `quota_check` | Checking a copy's bytes against the storage quota (near zero for empty creates) |
| `register` | The registration write transaction, including any unit acquisition from peers it needs |
| `cleanup` | Cleanup after a refusal, cancellation or failure in `prepare`, `quota_check` or `register` |
| `complete` | Clearing the engine's creation reservation after registration |

| `outcome` | Meaning |
|---|---|
| `completed` | The stage finished and the creation moved on. For `cleanup`, the cleanup call returned |
| `refused` | The stage ended the creation with a typed refusal (not found, conflict, quota, unavailable, including a storage capacity refusal). For `cleanup`, cleanup reported the catalog unavailable and left recovery work |
| `canceled` | The stage ended with `OperationCanceledException`, which propagates unchanged |
| `faulted` | The stage ended with any other exception, which propagates unchanged |

Nine stages by four outcomes gives at most 36 series. The exporter's cardinality limit is
37, which includes the reserved overflow series.

## Boundaries

- Each stage records once. It records when the next stage starts or when the creation
  returns or throws. A stage that fails records before its cleanup starts, so cleanup
  time is never counted in the failing stage. Cleanup records separately as `cleanup`.
- Cleanup inside `prepare` (finishing a published intent or releasing an unrecorded one)
  and cleanup after `quota_check` or `register` both record as `cleanup`.
- Time spent in the HTTP endpoint before the service is called (request validation,
  the endpoint's own serving-region and replica lookups, and project resolution) is not
  timed here. Compare with `http.server.request.duration` for the same window to see
  that gap.
- Instrumentation changes no operation order, retry, cancellation token, cleanup or
  resource ownership. Exceptions propagate unchanged.

## Limits

- The histogram is cumulative and aggregate. It cannot be tied to one customer request,
  tenant or database without private identity, which it never records. Correlate by
  time window and node only.
- Stages that wait on peers (`serving`, `precheck`, `register`) include network time to
  those peers. Their duration does not isolate local work.
- It makes no performance claim. Bucket boundaries go up to 60 seconds, the longest
  observed runner deadline.
