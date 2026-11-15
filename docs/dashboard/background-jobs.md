# Dashboard background jobs across instances

All regions run the same workers. The background_jobs table in the dashboard's TerraBase
database owns renewable claims. Acquiring an expired claim changes its owner with a
compare-and-set on the owner and checkpoint. A live owner renews every 20 seconds for a
two-minute lease; failed renewal cancels its work. Every checkpoint save also updates
the claim row in the same EF Core transaction as the job's state changes. A successor's
owner fences a stale checkpoint. Shutdown releases the claim; process failure leaves
it recoverable after expiry. Dashboard SQL uses the existing authoritative storage
routing, regardless of which region acquired the work.

| Job | Previous risk | Current coordination |
| --- | --- | --- |
| Log drains | Multiple instances sent a range before saving its cursor; a crash replayed a changing batch without a delivery identity. | A claim per drain revision and database stores batch bytes, next cursor and a stable delivery ULID before sending. Idempotency-Key and X-TerraScale-Delivery carry that ULID. The cursor advances and the payload clears in one fenced transaction. OTLP attributes include each log ID. |
| Alert evaluation and delivery | Instances read the same pending events and sent them without a claim; completion conflicts were ignored. | A project claim covers evaluation and delivery. Webhook bytes are stored before sending, with the event ID as Idempotency-Key and X-TerraScale-Delivery. Each event commits under the owner fence. Pending events are retained until delivery completes rather than pruned at ten events. |
| Allowance emails | Multiple instances sent a pending allowance before conditionally changing its status. | A claim per allowance serializes sending and completion. Auth's existing owner-delivery key also deduplicates the organization, month, metric and threshold. |
| Plan reconciliation | Each instance ran reconciliation and kept its own publication cache. | One recurring reconciliation claim; per-organization publication claims and stored plan, attempt and success times replace the local cache. Plan setters remain idempotent desired-state operations. |
| Stripe binding refresh | Every instance refreshed all bindings on its own daily schedule. | One recurring daily claim, renewable throughout pagination. |
| Usage settlement and reporting | Every instance scanned months with its own in-memory resume cursor. | One recurring settlement claim with a persisted page cursor; the existing unique frozen-charge ledger, charge owner/expiry and Stripe idempotency keys protect dispatch, including direct report calls. |
| Deletion reconciliation | Instances replayed the same cleanup journal and cancellation concurrently. | One renewable cleanup claim. Existing erasure fences, deletion proofs, acknowledgements and cancellation state remain idempotent recovery records. Project cleanup removes retained drain payloads. |
| Expired storage rows and browser sessions | Instances swept the same rows and revoked the same expired sessions. | One recurring sweep claim covers storage deletion and auth revocation. Expiry predicates, conditional deletes and token revocation remain idempotent during recovery. |
| Storage initialization | Instances can start migrations together. | The existing EF migration lock serializes schema application. |

The auth attempt limiter always uses its persisted store and fails closed on storage
failure. It grants no memory-only fallback budget. Auth webhook delivery already
claims endpoint and delivery versions, stores a two-minute lease and attempt before
sending, and completes against those claimed versions. Its webhook-id remains the
event ID on recovery.

External exactly-once effects require receiver cooperation. A receiver must retain
delivery IDs and reject conflicting bytes for the same ID. HTTP can repeat after
acceptance when the sender dies before committing; these repetitions use the same
key and payload. Arbitrary OTLP endpoints and SMTP do not promise durable idempotency.
The owner email service preserves its existing unknown-outcome semantics rather than
pretending that an SMTP acceptance and a TerraBase commit are atomic.

The billing journey includes provider acceptance before lost completion, charge
contention and report races. The background journey starts independent dashboard
application processes against the same real stack, holds an idempotent receiver
after acceptance, kills the sender, advances the journey clock past its lease and
checks one receiver effect from two byte-identical attempts. Another case holds an
alert while a rival runs and checks one webhook, one owner email and an empty pending
queue. No production switches, test hooks, sleeps, retries or larger timeouts are added.
