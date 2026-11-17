# Functions pricing study

## Decision

On 2026-11-17 the founder chose the shared operations bucket (option A below), superseding the option B recommendation:

- Each function invocation is one operation unit in the organization's monthly operations bucket, the same bucket as write units.
  The bucket is one combined count: write units + invocations.
- There is no CPU or duration meter. Customers never calculate CPU milliseconds.
- Free includes 50,000 operations a month, combined, and stops at 50,000. Paid plans keep their included operations,
  overage rate and hard caps from the catalog; invocations count toward the same numbers.
- SQL reads inside a handler stay under ordinary read fair use. Writes inside a handler are ordinary write units.
  The invocation itself is the only extra unit.
- Every invocation whose handler starts counts, from HTTP or cron, including handler errors, timeouts and cancellation.
  Auth rejection, a missing function, a refused allowance and a refused memory or capacity admission count nothing.
- The units per invocation are catalog data (`metering.functionInvocation.operationUnits` in `pricing.json`, today 1),
  so the price changes with one edit.

Implemented in TerraBase usage metering (`UsageMeter.RecordInvocation`, monthly `invocations` next to write units, peer
aggregation, `OperationAllowance`) and the allowance contract `operation-units-writes-invocations-global-v3`, whose
`operations` is the combined count and whose `writeUnits` and `invocations` show its parts. See
[operation allowance](../terrabase/operation-allowance.md).

The rest of this file is the study as written before the decision.

Internal proposal. Read date: 2026-11-17. USD, excluding taxes and negotiated discounts.
Code baseline: `410e56a302648e3705e472d4d1ed38ace510e6f9`.

## Recommendation before the decision

Recommend a separate invocation allowance plus requests and active CPU overage (option B).
Keep ordinary SQL write and storage charges. SQL reads remain subject to fair use.
Do not sell the CPU meter until attribution, durable accounting and quota enforcement exist.
The current implementation does not bill an invocation as an operation, despite the design study.
The measurements below estimate execution cost, not production gross margin.

## What happens today

| Evidence | Implemented behavior |
| --- | --- |
| [Design](design.md), “Auth, billing, secrets and logs” | Says one `invoke` operation plus normal SQL reads and writes. This is intent, not the invoice contract. |
| `src/TerraBase/Features/Functions/FunctionService.cs:132` | HTTP and cron share `RunAsync`. Creates a real SQL session, runs the handler, then calls `RecordOperation(tenant, "invoke", 1)` at line 157. |
| `src/TerraBase/Features/Management/Usage/UsageMeter.Measurements.cs:26` | `RecordOperation` updates request-log bookkeeping, then checks an observation dimension. It does not increment monthly billable units. |
| Same file, `ValidDimension`, line 118 | The allowed `ops` kinds are `read`, `put`, `patch`, `delete`. `invoke` is rejected. The persisted observation series also loses it. |
| `src/TerraBase/Features/Management/Usage/UsageMeter.cs:107` | `RecordRead` and `RecordWrites` update monthly read and write units separately. |
| `src/TerraBase/Features/Management/Usage/UsageService.cs:200` | Allowance response uses `units.Writes` for operations and declares `write-units-4kib-global-v2`. No invocation or CPU field. |
| `src/TerraScale.Dashboard/Features/Billing/UsageOverageReporter.cs:27` | Reads that response. Freezes monthly write/storage quantities against recorded subscription terms. |
| `src/TerraScale.Dashboard/Features/Billing/OperationUnitOverage.cs:5` | Paid overage is `max(0, used - allowance)` at catalog operation rate; cents round once, away from zero. |
| `src/TerraScale.Dashboard/Common/Accounts/PlanCatalog.cs` | Reads the shared catalog in `landing-site/static/pricing.json`; no Functions price. |

A successful trivial handler adds no customer write units for execution.
Three SELECTs incur normal SQL read fair use, with no separate invocation charge.
Committed writes from a handler incur ordinary write units. Function deployment/log storage has separate internal work.
A handler that returns an error still reaches the `invoke` call. A missing function or refused memory reservation does not.
Cancellation before the runner completes can bypass that call. It is not a complete billing ledger.
Write caps do not bound pure CPU work in Functions today.
The design statement must be reconciled before any external promise. This PR does not edit C1’s design study.

### Plans and public promises

Sources: [pricing](../../docs-site/docs/pricing.md), [pricing index](../../docs-site/docs/pricing/index.md),
[usage accounting](../../docs-site/docs/concepts/pricing-philosophy.md), and the shared catalog, read 2026-11-17.
None of these public pricing sources mentions Functions. The index only links the other pages.

| Plan | Monthly / yearly | Included write units | Monthly write cap | SQL read fair use | Storage | Client egress |
| --- | --- | --- | --- | --- | --- | --- |
| Free | $0 / $0 | 50,000 | 50,000 | 500,000 | 0.1 GB | 1 GB |
| Starter | $3 / $30 | 250,000 | 2,500,000 | 2,500,000 | 1 GB | 5 GB |
| Pro | $20 / $200 | 2,000,000 | 20,000,000 | 20,000,000 | 10 GB | 50 GB |
| Team | $50 / $500 | 5,000,000 | 50,000,000 | 50,000,000 | 25 GB | 100 GB |
| Enterprise, catalog only | $999 / not offered | 10,000,000 | Unlimited | Unlimited | 100 GB | 250 GB |

Enterprise has `available: false` in the catalog and is not a selectable dashboard subscription.
A committed write bills the greater of changed rows and started 4 KiB database growth units.
Failed, refused and rolled-back statements bill no write units. Reads are not billed.
Paid overage is $5/M write units and $0.50/GB-month above included storage.
Free has no overage. New writes stop at the write cap; SQL reads stop at fair use.
One region’s writes/storage are metered; copies are not charged again. Egress overage is not billed yet.
Customer-set dollar spending caps are coming; internal report-only thresholds do not cap a bill.
“Operations-only as today” would therefore mean write-only charging, not charging each invocation.
Option A below explicitly implements the design’s intended invocation charge.

## Competitor list prices

All source links in this section were read on 2026-11-17.
These are current public offers, not effective prices after credits or enterprise negotiations.
M = 1,000,000. A CPU-hour is 3,600,000 CPU-ms. A GB-hour is 3,600 GB-s.
Memory duration is not interchangeable with active CPU. Regional rates are examples, not worldwide quotes.

| Provider / unit | Free or included allowance | Request price per M beyond allowance | Compute price beyond allowance | Limits and material billing differences |
| --- | --- | --- | --- | --- |
| [Cloudflare Workers](https://developers.cloudflare.com/workers/platform/pricing/) / requests + active CPU-ms | Free: 100k/day, 10 ms CPU/request. Paid $5/month: 10M requests + 30M CPU-ms | $0.30 | $0.02/M CPU-ms | No wall-duration charge. Paid CPU defaults to 30 s; configurable to 5 min for HTTP. 128 MB memory. [Limits](https://developers.cloudflare.com/workers/platform/limits/). |
| [Vercel Functions, fluid compute](https://vercel.com/docs/functions/usage-and-pricing) / requests + active CPU-hour + provisioned GB-hour | Hobby: 1M requests, 4 CPU-hours, 360 GB-hours. Pro uses on-demand billing offset by plan usage credit | $0.60 | US `iad1`: $0.128/CPU-hour + $0.0106/GB-hour; `gru1`: $0.221 + $0.0183 | CPU stops billing during I/O; memory continues across instance lifetime and concurrent requests share it. Hobby 300 s; Pro 800 s max, extended beta 1,800 s. 2/4 GB memory. [Limits](https://vercel.com/docs/functions/limitations). |
| [Netlify Functions](https://www.netlify.com/pricing/) / shared credits, GB-hour and web requests | Free 300 credits/month; Personal $9/1,000 credits; Pro $20/3,000 credits | 200 credits/M: $2 Personal or $1.3333 Pro at additional-credit rates | 10 credits/GB-hour: $0.10 Personal or $0.066667 Pro per GB-hour | Additional bundles: $5/500 Personal, $10/1,500 Pro. Requests include assets and functions. Compute includes Functions, agents and DB. Deploys and bandwidth also spend credits. Function time/memory limits not found on the [overview](https://docs.netlify.com/build/functions/overview/); verify before workload comparison. |
| [Supabase Edge Functions](https://supabase.com/pricing) / invocations | Free 500k/month; Pro starts $25/month with 2M included | $2 | No separate compute price found; execution covered by invocation price | 256 MB; 2 s active CPU/request; worker wall life 150 s Free / 400 s paid; 150 s request idle timeout. [Limits](https://supabase.com/docs/guides/functions/limits). |
| [Deno Deploy](https://deno.com/deploy/pricing) / requests + active CPU-hour + GiB-hour | Free 1M requests, 10 CPU-hours, 150 GiB-hours; Pro $20 includes 5M, 50 hours, 750 GiB-hours | $2 | $0.10/CPU-hour + $0.025/GiB-hour | Default 768 MB allocation; memory bills while app is loaded, including idle tail of about 20–30 s. Site announces sunset in April 2027. A migration-risk comparison, not a long-term anchor. |
| [AWS Lambda](https://aws.amazon.com/lambda/pricing/) / requests + allocated GB-s duration | 1M requests + 400k GB-s/month | $0.20 | US East examples: x86 $0.0000166667/GB-s; Arm $0.0000133334/GB-s, first duration tier | Duration includes waits and initialization, rounded to 1 ms. CPU scales with memory; 128–10,240 MB, 900 s max, default regional concurrency 1,000 subject to account quotas. HTTP gateway, logs and transfer can add charges. [Limits](https://docs.aws.amazon.com/lambda/latest/dg/gettingstarted-limits.html). |
| [Google Cloud Run functions, 2nd gen](https://cloud.google.com/run/pricing) / requests + allocated vCPU-s + GiB-s | Request-based tier: 2M requests, 180k vCPU-s, 360k GiB-s/month | $0.40 | Tier 1 active: $0.000024/vCPU-s + $0.0000025/GiB-s | Billing rounds to 100 ms. Allocated resources during requests, not JS active CPU. Minimum instances may add idle charges. HTTP 60 min; scheduled/task 1,800 s; events 540 s. [Limits](https://cloud.google.com/functions/quotas). |
| [Firebase Cloud Functions](https://firebase.google.com/pricing) / invocations + duration | Blaze: 2M invocations, 400k GB-s, 200k CPU-s/month; Spark has no Functions allocation | $0.40 | Firebase table links Google Cloud pricing; standalone per-unit compute rate not found there | Generation matters: Firebase’s table is not a substitute for the 2nd-gen Cloud Run resource model. Build, artifact storage and outbound networking can add charges. DB access has its own prices. |
| [Convex](https://www.convex.dev/pricing) / calls + action GB-hour beside DB | Free/Starter: 1M calls + 20 action GB-hours; Professional $25/developer/month includes 25M + 250 GB-hours | Starter $2.20; Professional $2 | Starter $0.33/action GB-hour; Professional $0.30; query/mutation compute included | Query/mutation user CPU limit 1 s, excluding DB work; Convex actions 30 min, Node actions 10 min. Convex action memory 64 MiB, Node 512 MiB. [Limits](https://docs.convex.dev/production/state/limits) show a different action rate; pricing page is this table’s source. |

Netlify dollar equivalents above value additional credits, not a promise of fractional bundle purchases.
Its Free pool could buy 1.5M requests or 30 GB-hours if nothing else consumed credits, not both.
Cloudflare’s $0.02/M CPU-ms equals $0.072/CPU-hour; Vercel `iad1` equals $0.03556/M CPU-ms.
Deno equals $0.02778/M CPU-ms. Our proposed $0.06/M CPU-ms is higher. It targets 70% execution contribution under the 3× cost sensitivity; willingness to pay is unmeasured.
Request-only products constrain CPU or subsidize a workload mix. Their request prices do not imply unlimited compute.

## Cost on our hosts

Functions execute in the TerraBase process, on the shared ts-orch hosts in `gru`, `sjc`, `fra`.
No extra service or paid host is assumed. Execution happens once, not once per region.
SQL writes, logs and persistence may cause replication work outside that execution.

| Input | Evidence / assumption |
| --- | --- |
| Configured fleet | `ts-orchestrator` `origin/main` at `a3e98f83b985a49d94ede0f568ba6ab334e3b529`, `deploy/fleet/fleet.json`: each region 3 nodes, A1 Flex, 2 OCPU, 12 GB RAM. Desired config, not a live instance inventory. |
| OCI CPU | [Compute price list](https://www.oracle.com/cloud/compute/pricing/) and [USD catalog](https://apexapps.oracle.com/pls/apex/cetools/api/v1/products/?currencyCode=USD&partNumber=B93297,B93298), read 2026-11-17: A1 $0.01/OCPU-hour; Arm OCPU = one vCPU. |
| OCI RAM | Same catalog: $0.0015/GB-hour. Catalog updated 2026-11-02. These are list rates after free allowances, before taxes/discounts. |
| Host allocation | $0.038/host-hour = 2 × $0.01 + 12 × $0.0015. 730-hour month: $27.74/host; 9 hosts: $249.66 compute + RAM. |
| Runtime bounds | `FunctionRunner.cs`: fresh Jint engine per invocation; 10 s wall, 32 MiB allocated-memory limit, 10M statements, recursion 256. `FunctionSql.cs`: 256 SQL calls. The Jint allocation limit is not a process RSS limit. |
| Cost per busy core | Allocate all host RAM proportionally to its 2 cores: $0.019/core-hour = $0.00000527778/CPU-s. Includes RAM once. |
| Idle capacity sensitivity | Show full-use allocation and a 3× factor for spare capacity, architecture differences and omitted per-request work. This is a sensitivity, not measured fleet utilization. |
| Excluded costs | Disks, VPUs, backups, replication, egress, edge, logs outside measured scope, payment fees, support and engineering. No OCI free credit is assumed. |

| Handler | Mean CPU-ms/call | CPU-s/M | CPU + RAM cost/M | At 3× allocation |
| --- | --- | --- | --- | --- |
| trivial | 35.7333 | 35,733.33 | $0.1886 | $0.5658 |
| three-selects | 36.3667 | 36,366.67 | $0.1919 | $0.5758 |

Cost per M = measured CPU-ms/invocation × 1,000 × $0.019 / 3,600.
Pure CPU-only cost at $0.01/OCPU-hour is lower; using it alone would omit the hosts’ RAM.
A ten-second CPU-bound invocation costs about $0.00005278 at full use, or $52.78/M.
At 3× allocation that becomes $158.33/M. A cheap flat request tariff cannot cover this tail.
Ten seconds waiting on SQL also holds an engine and a 32 MiB memory reservation even when CPU is idle.
At 1M such calls, 32 MiB reserved for 10 s each is about 86.8 GiB-hours, before runtime overhead.
Active CPU pricing alone does not recover all scarce memory/concurrency cost during waits.

## Common example customers

| Customer | Plan | Invocations/month | Handler alternatives | SQL read units in three-query case |
| --- | --- | --- | --- | --- |
| Small | Starter, $3 | 100,000 | Trivial, or three indexed one-row SELECTs | 300,000 of 2.5M fair use |
| Medium | Pro, $20 | 5,000,000 | Same alternatives | 15M of 20M |
| Large | Team, $50 | 10,000,000 | Same alternatives | 30M of 50M |

Assume no other usage, monthly subscriptions, no billable SQL writes, and storage/egress inside allowances.
CPU uses the measured mean for each handler. Failed calls and cron must consume the same function allowance.
SQL row counts, not simply the number of queries, determine real read units; these SELECTs return one row each.
Today, all three examples pay only their plan fee: $3 / $20 / $50, with no invocation overage.
Bills below include the plan fee and Functions overage. They exclude taxes.
“Contribution” is revenue less modeled execution cost. It is not company gross margin.
Percentages use `(whole bill − execution cost) / whole bill`. At included usage the plan also funds the database.
Assigning its whole fee to Functions overstates margin; these are execution-only upper bounds.

## Option A: put invocations in the shared operations bucket

Implement the design intent: each admitted invocation consumes one operation unit, plus normal SQL writes.
Keep the $5/M overage rate and existing plan allowance. This changes today’s write-only contract.
Free stops at 50k combined units. Paid combined usage stops at each existing write cap.
In the examples, call counts stay below those caps. SQL reads remain on their separate fair-use meter.

| Customer | Bill: trivial / three SELECTs | Execution contribution: trivial / three SELECTs | At 3× cost: trivial / three SELECTs |
| --- | --- | --- | --- |
| Small | $3.00 / $3.00 | 99.37% / 99.36% | 98.11% / 98.08% |
| Medium | $35.00 / $35.00 | 97.31% / 97.26% | 91.92% / 91.77% |
| Large | $75.00 / $75.00 | 97.49% / 97.44% | 92.46% / 92.32% |

Once included quantities are exhausted, marginal execution contribution is 96.23% trivial / 96.16% three SELECTs at full-use cost.

Metering work: introduce an explicit combined-operations contract rather than relabeling write history.
Change `FunctionService.RunAsync`, monthly counters in `UsageMeter.Month.cs`, allowance enforcement in
`Features/Management/Usage/OperationAllowance.cs`, and the allowance API in `UsageService.cs`.
`PlanCatalog`, recorded billing terms, `UsageOverageReporter` and usage presentation must agree on the new unit.
Add a committed migration if persisted billing terms change; never edit existing migrations.
Merely allowing `invoke` in `ValidDimension` does not make it billable.
Risk: makes a 1 ms handler and a 10 s CPU handler cost the same. Shared caps can stop database writes.
A $5/M marginal price loses money on the ten-second CPU case even at full host utilization.
It is also expensive compared with $0.20–$2/M request-only competitors for short handlers.

## Option B: invocation allowance plus requests and active CPU

Keep database write pricing separate. Proposed invocation allowances: Free 50k, Starter 250k, Pro 2M,
Team 5M, Enterprise 10M/month. Proposed included CPU-seconds: 5,000 / 25,000 / 200,000 / 500,000 / 1,000,000.
Paid overage: $0.30/M invocations + $0.06/M active CPU-ms. Free stops when either allowance is exhausted.
Included CPU equals 100 ms per included call, in an independent monthly pool; SQL waiting does not consume CPU.
A heavy handler can exhaust CPU before requests. These are two ceilings, not a guarantee of that many included calls.
The modeled CPU price floor for 70% contribution at 3× cost is $0.05278/M CPU-ms; $0.06 rounds above it.
Aggregate CPU microseconds for the month, then convert; do not round each short invocation to a billed millisecond.
Bill admitted handler attempts, including errors/timeouts and cron; do not bill auth rejection, missing function or admission refusal.
These are proposed commercial rules, not current product behavior.

| Customer | Bill: trivial / three SELECTs | Execution contribution: trivial / three SELECTs | At 3× cost: trivial / three SELECTs |
| --- | --- | --- | --- |
| Small | $3.00 / $3.00 | 99.37% / 99.36% | 98.11% / 98.08% |
| Medium | $20.90 / $20.90 | 95.49% / 95.41% | 86.46% / 86.22% |
| Large | $51.50 / $51.50 | 96.34% / 96.27% | 89.01% / 88.82% |

Once included quantities are exhausted, marginal execution contribution is 92.28% trivial / 92.27% three SELECTs at full-use cost.

Formula: plan + $0.30 × max(0, N − included calls)/M
+ $0.06 × max(0, total CPU-ms − included CPU-seconds × 1,000)/M.
At the ten-second CPU tail, marginal revenue is $600.30/M against $52.78/M modeled cost.
That is 91.2% execution contribution at full use, 73.6% at 3× allocation, before excluded costs.
A 10 s wall limit is still mandatory. CPU billing cannot cure shared-process exhaustion or SQL waits.

Metering work: execution-boundary attribution in `FunctionRunner.RunAsync` and `FunctionSql.CallAsync`;
invocation admission/outcome handling in `FunctionService.RunAsync` and `FunctionScheduler`.
A process-wide CPU stopwatch is invalid under production concurrency. Thread switches and SQL work complicate attribution.
Choose and prove whether CPU covers JS only or JS plus SQL execution; the examples assume both once, including runtime work.
Do not mistake `FunctionLogBody.DurationMs` for CPU; it is wall time and logs retain only 100 runs.
Add durable per-organization/month invocation and CPU counters, peer aggregation, idempotent settlement and API fields
through `UsageMeter*`, `UsageService`, shared contracts and all consumers.
Dashboard needs plan allowances, recorded rates/terms, ledger meter kinds, invoice lines and clear usage quantities
through `PlanCatalog`, billing persistence, `UsageOverageReporter` and `StripeBillingOverage`.
Add journey proof for HTTP, cron, errors, timeout, caps, month rollover and invoice reconciliation.
Risk: inaccurate attribution creates billing disputes; a dropped observation is unacceptable as a financial record.
The proposed Free quota is smaller than major competitors’ free request pools. It may push hobby users away.
Tiny fixed call charges can still subsidize memory-heavy SQL waits. Fixed per-tenant concurrency admission must protect DB service.

## Option C: separate request quota and $1/M overage

Use B’s invocation allowances, with no CPU charge. Keep all database write/read rules separate.
Free stops at its included calls. Paid plans need a fixed monthly invocation cap and fair admission, specified before sale.
The example workloads must remain within that future cap; no automatic unbounded paid execution is implied.

| Customer | Bill: trivial / three SELECTs | Execution contribution: trivial / three SELECTs | At 3× cost: trivial / three SELECTs |
| --- | --- | --- | --- |
| Small | $3.00 / $3.00 | 99.37% / 99.36% | 98.11% / 98.08% |
| Medium | $23.00 / $23.00 | 95.90% / 95.83% | 87.70% / 87.48% |
| Large | $55.00 / $55.00 | 96.57% / 96.51% | 89.71% / 89.53% |

Once included quantities are exhausted, marginal execution contribution is 81.14% trivial / 80.81% three SELECTs at full-use cost.

Metering work: the same reliable invocation counter, API, allowance and dashboard settlement work as B, without CPU fields.
Use the same file boundaries. It is smaller than B, but still cannot use request logs as the billing ledger.
Risk: a ten-second CPU-bound million calls costs $52.78 at full use versus $1 incremental revenue.
The 10 s wall limit bounds one call, not monthly exposure. Cron can produce repeated expensive work without public traffic.
A shorter CPU budget would change which workloads fit. Do not silently tighten limits to make the price appear profitable.

## Recommendation and founder decisions

Choose B as the target tariff. Its request rate is competitive and its CPU rate prices the expensive tail.
The measured examples are a planning input; they do not establish production Arm performance or full service cost.
Keep existing write-only billing until a complete function ledger and accurate CPU attribution are journey-proven.
Do not advertise “one billed operation per invocation” while that contradicts the code and public write-only promise.
Before launch, run the same workloads on A1 with realistic concurrency, SQL sizes, writes and memory pressure.
Measure idle capacity, log/persistence overhead and SQL waiting separately; revisit allowances against those results.
No public page, catalog or Stripe configuration changes in this PR. No pricing is published yet.

| Founder decision | Proposed answer / tradeoff |
| --- | --- |
| Separate Functions charge or bundled database product? | Separate allowances and invoice quantities; avoid consuming customers’ write caps. |
| CPU scope and failed-call policy? | Charge admitted attempts and active execution including attributable SQL CPU once. Never bill rejected admission. Requires a credible meter. |
| Target contribution and free subsidy? | Aim for at least 70% execution contribution on ordinary traffic; the 3× ten-second tail reaches about 74% before excluded costs. Decide whether that safety allowance and the price premium are acceptable. |
| Spending promise and Enterprise terms? | Fixed documented plan caps until real customer dollar caps exist; decide those invocation/CPU ceilings and Enterprise commercial terms before launch. |
| Timing? | Approve the commercial direction now; publish only after accounting and capacity evidence exist. |

## Appendix: measurement method and raw samples

Measured on the local three-service journey stack. No production traffic or credentials were used.
The measurement driver lives outside the repository. Only this study is committed.
Host: x86-64 Intel Xeon E5-2660 v4 at 2.00 GHz; .NET 11.0.100 Release, Jint 4.17.0.
This is not an A1 benchmark. CPU scheduling contention affects wall time; Arm cost extrapolation is an assumption.
All preparation, services and driver ran under `nice -n 19`, with `DOTNET_PROCESSOR_COUNT=4`.
The documented Nix shell supplied missing `certutil` and `psql`. Only the three services and local TLS setup were prepared.

Reproduce in one shell session, from this checkout. Set E2E_STATE=/tmp/fb5d260b-C6-stack,
DOTNET_PROCESSOR_COUNT=4, the pinned DOTNET_ROOT, and PATH as in the brief. Use the Nix shell for all steps.
The prepare command reproduces required service/TLS artifacts; this study built only those artifacts, omitting unrelated journey helpers.

```sh
export PATH="$HOME/.local/bin:$HOME/.local/share/mise/shims:$PATH"; nice -n 19 env DOTNET_PROCESSOR_COUNT=4 DOTNET_ROOT="$HOME/.local/share/mise/dotnet-root" E2E_STATE=/tmp/fb5d260b-C6-stack nix shell nixpkgs#nss.tools nixpkgs#postgresql -c bash -s <<'SH'
set -euo pipefail
bash e2e/env/local.sh prepare
bash e2e/env/local.sh start
trap 'bash e2e/env/local.sh stop' EXIT
bash e2e/env/local.sh exec bash e2e/env/guard.sh
bash e2e/env/local.sh exec bash e2e/env/up.sh
bash e2e/env/local.sh exec bash -c 'cd e2e && npx playwright test --project=desktop-setup --no-deps'
bash e2e/env/local.sh exec python3 /tmp/fb5d260b-C6-measure.py
SH
```

Recreate the external Python driver from the algorithm below; its temporary path is illustrative.
The normal journey setup creates a real organization, database and project API key.
The driver exchanges that key with ts-auth, deploys through authenticated REST, and invokes through the local HTTPS edge.
Project tokens last 60 s (`PlatformGrants.Lifetime`). Renew at expiry minus 10 s, before sending a request; never retry failed requests.
An initial incomplete attempt hit token expiry. It is excluded in full; all six final batches below completed without HTTP errors.
Setup creates `pricing_probe (id INTEGER PRIMARY KEY, value INTEGER NOT NULL)` and inserts `(1, 7)`.
Each query is `SELECT value FROM pricing_probe WHERE id = ?` with `[1]`.
The first handler returns `{ok: true}`. The async handler performs three sequential `sql.query` calls and returns their values summed.
Every measured response was checked: HTTP 200 and `{ok: true}` or `{value: 21}`.
Warm each handler with 150 invocations, filling its 100-log retention window. Alternate trivial and three-SELECT batches for three rounds, 200 sequential calls per batch.
Read TerraBase’s `/proc/<pid>/stat` before and after each batch. Add user and system CPU ticks for the whole process.
Convert with `SC_CLK_TCK=100`; divide by 200. Client, TLS edge and auth process CPU are excluded; any auth-related storage work in TerraBase is included.
Server SQL, fresh Jint engine, source parsing, result serialization, persistent invocation logs and overlapping background work are included.
No idle subtraction, per-thread CPU attribution, GC exclusion or favorable-sample selection. No CPU time is inferred from wall duration.
The process counter includes all service threads; use it for isolated cost sampling, never per-customer production billing.

| Handler | Run | Calls | User ticks | System ticks | CPU seconds | Batch wall seconds | CPU-ms/call |
| --- | --- | --- | --- | --- | --- | --- | --- |
| trivial | 1 | 200 | 511 | 122 | 6.33 | 6.766339 | 31.6500 |
| three-selects | 1 | 200 | 631 | 142 | 7.73 | 8.555048 | 38.6500 |
| trivial | 2 | 200 | 684 | 169 | 8.53 | 18.182556 | 42.6500 |
| three-selects | 2 | 200 | 606 | 141 | 7.47 | 8.932745 | 37.3500 |
| trivial | 3 | 200 | 523 | 135 | 6.58 | 9.582699 | 32.9000 |
| three-selects | 3 | 200 | 528 | 134 | 6.62 | 7.692429 | 33.1000 |

The mean uses all 600 measured invocations per handler, after warm-up.
Ranges overlap: trivial 31.65–42.65 CPU-ms; three SELECTs 33.10–38.65 CPU-ms.
Do not infer the incremental cost of three queries from the small difference between these noisy means.
Tick resolution is 0.05 CPU-ms/call per 200-call batch. No confidence interval is claimed from three batches.
One hot indexed row is a lower-cost database case. It does not represent scans, writes, large results, cold storage or replication.
The sequential sample says nothing about saturated concurrency or the maximum sustainable throughput.
All services started for this study were stopped with `local.sh stop`; measurement tables retain no tenant IDs, tokens or secrets.
