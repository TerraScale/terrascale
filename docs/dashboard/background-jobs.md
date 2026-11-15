# Dashboard background jobs across instances

Every region runs the same workers. The dashboard stores claims in background_jobs
in its TerraBase database. Acquisition is one conditional upsert: the database clock
must show both the lease and the next-run deadline expired. A new owner and checkpoint
fence the previous owner. Renewal is one conditional update requiring the current
owner and an unexpired lease. Both operations calculate the two-minute lease in SQL.
Completion calculates the next run in SQL, bounded to one day from database time;
checkpoint saves ignore regional lease/deadline values. Wall-clock skew in a dashboard
process cannot take a live claim or extend its lease or schedule.

Renewal runs every 20 seconds. A transient storage error retains the last proven
lease, logs the failure, and leaves current work running. Monotonic elapsed time and
a bounded renewal request budget ensure work is canceled before that proof expires.
A changed owner cancels work immediately. Every checkpoint updates its concurrency
token in the same EF Core transaction as the corresponding business changes.

| Job | Previous risk | Coordination |
| --- | --- | --- |
| Log drains | Instances sent a range before saving a cursor; interrupted sends had no stable identity. | Read the source first; empty ranges without a gap do not claim or write. A claim stores source bytes, delivery ID and next cursor before sending. Idempotency-Key and X-TerraScale-Delivery preserve that ID. Cursor and completion commit together. Transport/URL errors record No response with status zero. Old revisions and deleted projects remove retained claims. |
| Alerts | Instances evaluated and delivered the same pending events. | Read-only evaluation identifies changes or pending events before claiming the project. Checkpoint observations fence stale evaluations. Stored webhook bytes and event IDs survive interrupted delivery. Keep ten events per rule, including a currently claimed event. |
| Allowance notifications | Instances sent the same pending allowance before changing its status. | Claim each pending allowance before sending and fence completion. Auth retains its existing owner-delivery deduplication key. Removed allowances also remove their claims. |
| Plan reconciliation | Each instance reconciled and cached publication attempts independently. | A read-only pass checks receipts, checkouts, grace deadlines and persisted publication state before claiming work. Publication reads persisted state before claiming, stores failure counts and exponential backoff, and writes nothing on a settled view. Claim failures use the optional publisher error boundary. Onboarding uses the existing idempotent organization setter without failing on claim contention. |
| Stripe binding refresh | Every instance independently refreshed all bindings. | A recurring daily claim covers pagination. |
| Usage settlement/reporting | Each instance scanned with a local resume cursor. | A shared recurring claim and persisted cursor cover settlement. The worker records a completed pass only after acquiring its claim. Existing frozen charges, charge claims, provider recovery and Stripe idempotency keys protect dispatch. |
| Deletion cleanup | Instances replayed cleanup and cancellation concurrently. | A renewable cleanup claim covers existing erasure journals, proofs and idempotent cancellation. Claim-lifecycle outages are logged at the worker boundary. Organization cleanup deletes publication and allowance claims. |
| Storage/session sweep | Instances deleted the same expired rows and revoked the same sessions. | A recurring claim covers existing conditional deletion and idempotent revocation. |
| Storage initialization | Instances can migrate concurrently. | The existing EF migration lock serializes schema application. |

Auth admission is unchanged by this PR. Transient storage failures retain the existing
bounded per-replica fallback. Auth webhook delivery already persists endpoint and
delivery version claims before sending, completes against those versions, and retains
the same webhook-id on recovery.

External exactly-once effects require receiver deduplication. A receiver must retain
delivery IDs and reject conflicting bytes for the same ID. A crash after acceptance
can repeat an HTTP send with the same key and bytes. Arbitrary OTLP endpoints and
SMTP do not promise durable idempotency; owner email keeps its existing unknown-outcome
semantics.

The journey starts independent dashboard application processes against real TerraBase
state isolated from the stack's scheduled dashboard worker. SQL activity reaches the
production log source; the worker persists its range before sending. The journey kills
that sender after receiver acceptance and proves identical recovery, a real stale
Claim.SaveAsync refusal, skewed-clock bounds, a lost real renewal reply, read-only idle
passes, one alert webhook and one owner email. It adds no production hooks, sleeps,
retries, raised timeouts or weakened assertions.

Rolling back leaves billing jobs idle through BillingSchemaGate until roll-forward.
During a mixed-version deployment, old instances still run background work unclaimed.
