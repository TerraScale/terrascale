# Regional write latency: SJC client, GRU home

Three final SJC repeats pass: default origin p50 **11.86–14.38 ms**, default
client p99 **52.22–69.35 ms**, and opt-in home-durable client p99
**252.65–345.40 ms**. Durability, regional partner hold, R12 clock admission
and home lease fencing are preserved. Code shipped at `a2de2bdeb` (HLC) and
`ca3063480` (initial lease); final serving source is `80303f9d3`.
The separate forced-home comparison has one **409.76 ms** tail, tracked in
[#1103](https://github.com/TerraScale/terrascale/issues/1103).


## Before local acknowledgements: exact server correlation

Serving source `d38f9b02e` includes the read owner's fixes. Three additional
measured repeats on SJC used 40 writes per mode through reused HTTPS. Every
sample's response `X-Request-ID` matched its SJC request-log observation, read
through the existing authenticated internal logs endpoint. No observation was
missing. The service's `latencyMs` measures the origin pipeline through response
completion, including admission and execution, without client TLS or the edge.
This supplies the server measurement before the new `Server-Timing` stages
exist. Its window starts in usage middleware, before routing, authentication
and admission, but after the outer request-identity and pressure wrappers.
The new `app` span starts earlier, at the outer SQL middleware, and ends at
response headers. Paired log measurements remain the comparable before/after
series; the header spans additionally partition origin work.

All regions retained the same source around each repeat; all 360 measured rows
reached GRU exactly once, and each database was deleted.

| Repeat | Mode | Server p50 ms | Server p99 ms | Client p50 ms | Client p99 ms |
| --- | --- | ---: | ---: | ---: | ---: |
| 1 | default | 205.66 | 232.00 | 207.82 | 233.67 |
| 1 | durable_home | 223.03 | 263.64 | 224.89 | 265.40 |
| 1 | forced_home | 199.16 | 386.47 | 201.09 | 388.11 |
| 2 | default | 196.06 | 373.12 | 197.93 | 374.99 |
| 2 | durable_home | 222.37 | 267.35 | 224.27 | 269.65 |
| 2 | forced_home | 203.69 | 223.35 | 205.37 | 225.12 |
| 3 | default | 203.64 | 225.90 | 205.43 | 227.73 |
| 3 | durable_home | 223.42 | 262.12 | 225.32 | 264.36 |
| 3 | forced_home | 205.76 | 229.93 | 207.45 | 231.54 |

[Raw correlated samples](2026-11-05-regional-write-server-before.json) retain
request IDs, the paired server/client samples, routes and source snapshots.
The `durable_home` target was met in these repeats. Default writes still commit
home and exceed the local-ack targets. This phase is kept separate from the
earlier source's larger local-overlay tail.

## Earlier baseline before the read fixes

Three measured repeats on 2026-11-05 used serving source
`6cacd06cf63aa75d91eaac5d1230b550ffc87fae` in GRU, SJC and FRA. Authenticated
internal health around each repeat confirmed unchanged source revisions.
Each repeat inserted 40 rows per mode through a reused HTTPS connection and
40 per mode through the node's loopback client listener. Five warmup writes per
mode were excluded. All measured rows reached GRU exactly once; each proof
database was deleted. `ts-throwaway-account --delete fastwrites-20261105`
returned `DELETED`, and the private credential copies were removed from SJC.

The pre-instrumentation source emits a `home` span only for forwarded writes;
it does not emit total origin time or local home-durable server time. Those
cells are unavailable, not zero. The new diagnostic source will provide both.

| Repeat | Mode | Client p50 ms | Client p99 ms | Home span p50 ms | Home span p99 ms |
| --- | --- | ---: | ---: | ---: | ---: |
| 1 | default | 200.84 | 272.30 | 191.55 | 243.8 |
| 1 | durable_home | 424.40 | 509.00 | unavailable | unavailable |
| 1 | forced_home | 199.08 | 291.45 | 195.0 | 286.4 |
| 2 | default | 201.44 | 375.07 | 193.25 | 243.8 |
| 2 | durable_home | 226.60 | 472.97 | unavailable | unavailable |
| 2 | forced_home | 207.40 | 244.11 | 193.65 | 222.2 |
| 3 | default | 206.62 | 229.92 | 195.15 | 219.7 |
| 3 | durable_home | 223.26 | 541.34 | unavailable | unavailable |
| 3 | forced_home | 202.32 | 257.47 | 193.55 | 208.2 |

[Raw before samples](2026-11-05-regional-write-latency-before.json) include both
transports, warm health timings, routes, source snapshots and exact home counts.
With 40 samples per cell, p99 is the observed maximum under the R7 proof's
rounded order-statistic convention.

## Client and network attribution

The original R7 proof ran on delta with a new urllib HTTPS connection for
every request. Repeating that client's `/healthz` call, without a write,
took 829.0, 906.8 and 838.2 ms. There was no configured HTTP proxy. A curl
connection from delta took 633.7 ms for its first health response and
199.3 ms on reuse. From SJC, a reused public health connection was about
1.3 ms, and the loopback client listener about 0.7 ms. Direct authenticated
internal HTTP/2 health within SJC had a 0.49 ms median; SJC→GRU had a
169.12 ms median (20 samples, including the first connection).

DECISION: measure the 150 ms client target from a client in SJC. Reason:
delta's reused HTTPS network RTT alone is about 199 ms. Keep the original
remote-client result as a separate connection/proximity comparison.

The before results put ordinary forwarded writes near one SJC→GRU RTT plus
home processing. Home-durable writes through the local overlay have a larger
and variable tail, including through the loopback listener; public TLS and
the edge cannot account for that tail.

[Connection and health evidence](2026-11-05-regional-write-connections.json)
retains the raw internal samples and the exploratory SJC connection comparison.

## Excess local-overlay latency

Across these repeats the push-lag histogram gained 270 notices, matching the
270 local home-durable transactions (40 measured plus five warmups, over two
transports and three repeats). Their means were 191.51, 191.81 and 190.13 ms;
269 were at most 250 ms and all 270 at most 500 ms. This window starts at the
HLC stamp and ends when SJC receives the applied notice. It excludes work
before the stamp and response continuation after the notice. The 509–767 ms
client tails therefore include substantial cost outside the remote-apply
window.

Code inspection identifies a candidate write delay:
`TursoReplicas.Replica.PullAsync` holds `_pulls` while awaiting WAN prefetch,
and `Replica.BeginWriteAsync` waits for that same semaphore. This can put a
write behind a roughly 169 ms prefetch before its own acknowledgement work
starts. The new `local_begin` stage measures that wait separately. The read
owner retains the pull implementation; this report does not claim a measured
fix to that candidate until the stage timings confirm it.

## R10-only qualification before the lock fix

One additional 40-write qualification per mode ran on stable source
`59c46e4ac` in GRU/SJC/FRA, before the WAN-fetch lock fix and before the second
SJC node was active. It validates the default `commit=local` route, but is not
the final two-node proof. Every response ID matched its SJC request-log timing;
all 120 measured rows reached home exactly once and the database was deleted.

| Mode | Server p50 ms | Server p99 ms | Client p50 ms | Client p99 ms |
| --- | ---: | ---: | ---: | ---: |
| default | 45.93 | 576.72 | 47.87 | 578.89 |
| durable_home | 229.39 | 457.07 | 231.29 | 459.46 |
| forced_home | 197.45 | 225.76 | 200.00 | 228.12 |

Enabling the safe local acknowledgement route removes the default WAN apply
wait, but does not by itself meet the local latency targets. This qualification
keeps that remaining stall visible instead of attributing the whole improvement
to the routing gate. A `commit=local` route may still wait for home when its
required partner hold fails; route headers alone do not prove two-node
acknowledgement availability. The lag owner's `d450cbe3b` includes moving WAN prefetch
outside the replica write lock; the final stage timings measure the remaining
writer acquisition and acknowledgement work separately.

[Raw R10 qualification](2026-11-05-regional-write-r10-qualification.json).

A separate 20-write-per-mode qualification on stable `d450cbe3b`, including
`71511c0eb`, recorded default server p50/p99 **21.12/79.25 ms** and client
p50/p99 **22.61/80.96 ms**. Home-durable client p99 was **251.07 ms**. All
60 measured rows reached home exactly once and the database was deleted.
Both snapshots recorded one SJC node, `localwrites_available=1` and
`localack_partner=0`. This is intermediate one-node evidence, not the final
three-repeat proof.

[Raw lock-fix qualification](2026-11-05-regional-write-lag-qualification.json).

## Durability and scope

The baseline router's `LocalCommitAvailable` requires `homeDurable`, so an
ordinary request can be classified `route=local` while it commits at home.
The SQL endpoint also calls `CommitAsync(homeDurable: true)` unconditionally
on its local path. Together these make remote confirmation part of every
successful write, irrespective of the default local durability request.
The roughly 196–206 ms default server medians match this WAN dependency.

R10 owns default local acknowledgements and durable acknowledgement for the
discovered regional quorum: a one-node region acknowledges on that node;
with a discovered partner, both copies are required or the request waits for
home. N1 owns activating the second nodes. The probe records actual membership
and partner readiness; it never changes the quorum. This work preserves those
durability rules, plan admission and replica reads. The diagnostic header and
benchmark separate those dependencies from other write-path cost.

## Qualification attempts

The first harness attempt exposed a local Python import error before any
database was created; it was corrected. One exploratory three-repeat run
completed its first repeat but received `400 sql_error` in the next repeat
while production moved from `e661a352b` to `6cacd06cf`. Its database cleanup
completed. A separate second-repeat qualification succeeded. These exploratory
runs are excluded from the measured table. The reported three repeats record
source stability and complete home-count validation.

## Shipping and reset boundary

The write diagnostics, persistent-connection probe and forced-home snapshot
bypass shipped through `ts-ship` as `cced3a160ffd2f23604a05b3a37a0c19af56fe19`.
Its commits are `c66293b80` (SQL timing), `fbc94428a` (probe and timing docs),
`c674c42dd` (forced-home snapshot bypass) and `cced3a160` (baseline evidence).
The final candidate passed TerraBase Fast in 10.864 s and Auth Fast in 8.909 s;
benchmark Fast passed in 0.287 s. Main races caused required rebases and retests;
no gate was bypassed. CI completed successfully in [run 42034724081](https://github.com/TerraScale/terrascale/actions/runs/42034724081).

The second synthetic account, `fastwrites-logs-20261105`, returned `DELETED`.
Its local and SJC credential copies were removed. All raw evidence above was
preserved before the verify lane's announced format-23 production reset.
Authenticated health confirmed `cced3a160` serving in GRU, SJC and FRA on
format `22-41a88901325a` before the reset
([deployment snapshot](2026-11-05-regional-write-deployed.json)). New live writes
are held through the reset; final measurements will use a new account after
the reset handoff.

## Pre-reset stage probe and HLC correction

This diagnostic cohort ran on `ae8c2de66` in all three regions, format
`22-41a88901325a`, after the lock and acknowledgement-routing fixes. It is
preserved separately from the required post-reset proof. Three repeats used
40 measured writes per mode on a reused HTTPS connection from SJC. Every
response correlated with its server request-log observation, every measured
row reached home exactly once, and each proof database and the account were
deleted. SJC was a discovered one-node quorum (`localwrites_available=1`,
`localack_partner=0`); it did not acknowledge on an absent second node.

| Repeat | Mode | Server p50 ms | Server p99 ms | Client p50 ms | Client p99 ms |
| --- | --- | ---: | ---: | ---: | ---: |
| 1 | default | 26.56 | 77.64 | 28.63 | 79.63 |
| 1 | durable_home | 223.00 | 521.48 | 224.93 | 522.99 |
| 1 | forced_home | 197.32 | 240.45 | 199.07 | 242.09 |
| 2 | default | 21.90 | 86.57 | 23.89 | 89.56 |
| 2 | durable_home | 225.28 | 535.60 | 227.17 | 537.83 |
| 2 | forced_home | 197.53 | 232.75 | 199.38 | 234.76 |
| 3 | default | 22.84 | 72.88 | 25.56 | 75.05 |
| 3 | durable_home | 223.91 | 261.43 | 226.17 | 263.79 |
| 3 | forced_home | 196.49 | 213.94 | 198.23 | 215.14 |

[Raw diagnostic samples](2026-11-05-regional-write-pre-reset-stages.json).
The default client target passes; its origin median and the first two
home-durable tails do not. Default `home_lookup` medians were 3.82–4.12 ms,
`tenant` 4.44–4.88 ms and `ack` 8.52–9.50 ms. `NodeHlcClock` durably committed
the system database for every issued or observed stamp. That advanced the
system commit sequence and invalidated the catalog caches on every write.

The correction serializes timestamp issuance in memory and durably reserves
an upper bound five seconds ahead before returning any uncovered stamp.
Most writes avoid both the system transaction and its cache invalidation.
On restart the full reserved range is skipped. The guard sees the recovered
bound and keeps local admission closed until the existing 50 ms skew limit
permits it; current-process admission sees issued/observed time, rather than
the unused reservation. Observation still immediately advances the guard.
No quorum, local transaction durability or home acknowledgement rule changes.

DECISION: reserve five seconds of HLC time durably, and fail local admission
closed after restart until time catches up. Reason: retain durable coverage,
monotonic restart order and R12's pre/post admission and 50 ms skew guarantees
while removing per-write system commits. The availability tradeoff is up to
five seconds before local admission resumes; forwarding home remains available.

The two largest home-durable samples spent 501.34 and 503.96 ms inside `ack`,
against approximately 190 ms for its median. GRU's apply counter recorded four
temporary failures over the whole diagnostic window. Its retry loop delays
100 ms then 200 ms: consistent with the excess, but these aggregate counters
do not identify the failed operation or prove which sample retried. New
acknowledgement substeps separate capture, HLC, sealing, durable publish,
regional hold, local commit and home wait. Home apply also records attempt
time and bounded retry reasons. The final proof must distinguish that evidence
from this preliminary attribution.

## HLC shipment and safety validation

The HLC reservation and diagnostics shipped through `ts-ship` as
`a2de2bdeb8f3493e2c58952cfe8af144c786f506`, rebased onto the format-23 bump
`40b1dd36b`. The clock implementation is `358b76ae9`; acknowledgement timing
and home-apply diagnostics are `e01aa8805`; reservation-boundary coverage is
`277b9ba39`; peer push/cache snapshots are `a2de2bdeb`.

TerraBase Fast passed 62 service tests in 9.412 s (9.670 s including runner
completion), alongside four driver tests. Auth Fast passed 30 tests in
9.739 s (9.983 s including runner completion). Benchmark Fast passed in
0.254 s. The focused real-listener ClockSafetyTests ran on beta through
`ts-heavy-any`: two passed in 6.436 s. They cover concurrent unique issuance,
remote observation inside a reservation without rewriting its durable record,
renewal beyond the reserved bound, immediate admission closure on observing a
future stamp, and monotonic recovery plus skew refusal after a backward-clock
restart. The existing admission checks and assertions were retained.

[CI run 42049651489](https://github.com/TerraScale/terrascale/actions/runs/42049651489)
completed successfully, including the automatic all-region deployment.
Authenticated health on GRU/SJC/FRA confirmed `a2de2bdeb` serving format
`23-41a88901325a` ([deployment snapshot](2026-11-05-regional-write-hlc-deployed.json)).
This snapshot records one discovered SJC node, local writes available and no
regional partner. It is deployment evidence, not the final latency proof.

## Post-reset HLC diagnostic: local targets pass, durable tail still open

Two complete SJC repeats used 40 measured writes per mode, five excluded
warmups and one reused HTTPS connection. Serving source stayed `a2de2bdeb`
in all regions. SJC had one discovered node: local writes available, no
regional partner. Both proof databases were verified at GRU and deleted;
`fastwrites-hlc-final-20261105` returned `DELETED` at 15:30 UTC and its remote
credential copies were removed. A third repeat crossed deployment during its
final snapshot; its database was deleted, but it is excluded from this evidence.
These two repeats are diagnostic, not the required final three-repeat proof.

| Repeat | Mode | Server p50 ms | Server p99 ms | Client p50 ms | Client p99 ms |
| --- | --- | ---: | ---: | ---: | ---: |
| 21 | default | 7.43 | 17.00 | 9.12 | 20.63 |
| 21 | durable_home | 429.52 | 615.83 | 432.29 | 617.77 |
| 21 | forced_home | 203.88 | 401.39 | 205.83 | 403.20 |
| 22 | default | 6.31 | 10.13 | 8.16 | 11.49 |
| 22 | durable_home | 412.77 | 583.87 | 414.75 | 585.97 |
| 22 | forced_home | 202.57 | 237.33 | 205.04 | 241.89 |

[Raw HLC diagnostic samples](2026-11-05-regional-write-hlc-sjc-diagnostic.json).
Repeat 21 default stage medians were HLC 0.01 ms, tenant 0.17 ms,
home lookup 0.76 ms, durable publish 0.53 ms and local commit 2.88 ms.
The server median fell below 20 ms without changing commit durability.

The durable path's median home wait was 398.52 ms. GRU recorded 92 apply
attempts for 90 confirmed pushes in each repeat, including two temporary
`StorageBusyException` failures. Its attempt sums were 1137.239 ms and
1008.631 ms, or 12.36 and 10.96 ms per attempt. Home application is therefore
small relative to the wait. Decode called `DataKeys.GetAsync`, whose warm-key
path still read the remote shred record on every message. F11 owns removing
that roughly 210 ms remote dependency while preserving immediate revocation;
no custody code was changed in this work.

## First home-lease confirmation interrupts active writes

GRU's retry pairs occurred at 15:20:08.497/.599 and 15:21:08.444/.544 UTC,
with the unchanged 100 ms and 200 ms backoffs. They line up with the first
15-second lease scan after each database was created. `ConfirmAsync` added
an unheld lease before its WAN KV read/CAS; engine begin/commit checks then
correctly refused writes during confirmation.

Independent lag evidence retained the lease for database
`01M6P5DHXGD23RDC9CP319CDVD`: KV sequence 9666, written at
15:32:22.800329175 UTC. Its sequential writer's previous row was visible at
15:32:22.584347 UTC, followed immediately by a `409 serialization_conflict`.
The actual initial lease publication therefore overlaps the write failure,
corroborating the scan-phase attribution rather than relying on timing alone.

DECISION: confirm the initial lease during replicated database preparation,
including all-regions placement, before returning management success. Reason:
move the initial WAN CAS into setup while preserving the serialized confirmation,
expiry margin, failover CAS and engine checks at begin and commit. Later scans
reuse the confirmed session without closing its gate. The write retry policy
is unchanged. Temporary rate-limited stack diagnostics were removed once the
cause was corroborated; permanent bounded-reason retry counters remain.

The existing real-NATS failover test now asserts that creation has already
confirmed both explicit-replica and all-regions database leases. It then
continues through its partition, failover and acknowledged-write preservation
checks. The focused test passed on beta through `ts-heavy-any` at source
`0eaef8c2a`: one test, 47.634 s.

The diagnostic rollout `8811ba597` passed all Fast checks but stopped after SJC:
[CI 42057765641](https://github.com/TerraScale/terrascale/actions/runs/42057765641)
rejected the new SJC-2 task as running on an unexpected machine. N1 owns that
rollout fix. Final repeats wait for complete deployment of the lease fix and
F11's key-cache fix. At this boundary SJC discovers two nodes and reports a
healthy in-region partner; final proof must record the actual topology.

## Lease fix shipping and exact Libraries qualification

The lease fix shipped through `ts-ship` at
`ca30634800bba781f99ce3846dca6b37723fab02`, including lease preparation
`21834b712`, benchmark identity/timestamps `5d2a89a52` and temporary-stack
removal `ca3063480`, after rebasing on `9aa1c1bf6`. TerraBase Fast passed
in 11.950 s (62 tests plus four driver tests); Auth Fast passed in 8.600 s
(30 tests), and benchmark Fast was carried at 0.344 s over a disjoint change.

The official `ts-ship` runner separately qualified Libraries at that exact
main SHA through `ts-heavy-any`: Fast 7.671 s, checked build 54.137 s.
Both `ts-ship/libs` and `ci-build/libs` success statuses were published by
`MarioGK`. The checked-build manifest SHA-256 is
`8bd1050d5721ea4a7f0e742d58a2001dc9c4239893855dfca600aa852c1cfdfc`.
[Exact-commit receipt](2026-11-05-regional-write-libs-receipt.json).
This supersedes the missing receipt at the earlier diagnostic commit.

F11's prerequisite cache fix is `f7a8881d5`: warm decoding performs no R2
GET, while cache revocation remains acknowledged by all serving nodes.
The lease candidate includes this commit; this work makes no custody changes.

The final lease deployment completed successfully in
[CI 42065444915](https://github.com/TerraScale/terrascale/actions/runs/42065444915).
Authenticated health verified all six nodes (two each in GRU, SJC and FRA)
serving `ca3063480` and format `23-41a88901325a`.
[SJC mesh and deployment snapshot](2026-11-05-regional-write-lease-deployed.json)
records a reused SJC→GRU health median of 187.86–187.99 ms and healthy SJC
partners. The earlier four-node mesh snapshot remains separate historical
context in [mesh evidence](2026-11-05-regional-write-final-mesh.json).

The first final-proof attempt stopped before completing a repeat because the
new SJC-2 node returned 404 for an empty database log. Its database was deleted.
The collector now skips only per-node 404s while still requiring every measured
request ID; other HTTP errors fail the proof. All three measured repeats were
restarted. This changes proof collection, not write routing or server behavior.

One complete two-node SJC qualification repeat at stable `ca3063480` measured
server p50/p99 14.04/20.57 ms and client p50/p99 15.96/28.47 ms for default
local acknowledgements. Home-durable server p50/p99 was 227.31/248.87 ms,
client 229.52/250.89 ms; forced-home client p99 was 223.29 ms. SJC partner
hold median was 5.93 ms, and durable home-wait median 190.56 ms, close to the
187.86 ms mesh RTT. No GRU apply retry series was present before or after
(equivalent to zero retries since process start).
[Qualification raw data](2026-11-05-regional-write-lease-sjc-qualification.json).

The next repeat completed its writes and deleted its database, but a concurrent
rollout made a peer's health connection fail during the final snapshot. That
incomplete repeat is excluded; the collector now saves measured samples before
that snapshot and marks incomplete snapshots ineligible. All three final
repeats are restarted after the current rollout, within the coordinated main
hold through 17:12 UTC. No latency sample is removed from a completed repeat.

The subsequent `80303f9d3` release failed before mutating serving state:
[CI 42067463433](https://github.com/TerraScale/terrascale/actions/runs/42067463433)
could not download the selected orchestrator release asset (`no assets to
download`). All six nodes remained on `20609ac99`, a descendant of the lease
and cache fixes, with successful
[CI 42066502559](https://github.com/TerraScale/terrascale/actions/runs/42066502559).
The release-asset issue was handed to the parent. An already-running retry
then rolled forward to `80303f9d3`; final proof waits for all six nodes on
that revision. No infrastructure changes were made here.

The parent identified the 16:52:15–17 UTC ts-orch restart as the cause of the
peer connection refusals. A subsequent attempt stopped during its initial
snapshot before timed writes; its DELETE returned 503 with key destruction
pending. The previous FRA account (unused for measurements) was deleted.
Two replacement synthetic accounts stopped during onboarding with 503 while
the rollout was active; their private state is retained for safe resumption.
The fabric freeze was extended until 20 minutes after the in-flight
`80303f9d3` rollout finishes. Final runs wait for that exact revision on all
six nodes and preserve database IDs and cleanup status from creation onward.

## Final post-reset proof

All three SJC repeats meet the brief's targets on deployed
`80303f9d36c24cdea1976225bd234ecc4c40127a`, which includes HLC, lease and
F11 cache fixes. [CI 42067463433](https://github.com/TerraScale/terrascale/actions/runs/42067463433)
completed successfully on retry. Authenticated health verified all six nodes
(two per region), format `23-41a88901325a`, and unchanged revisions before and
after every repeat. Both SJC nodes reported a healthy regional partner.
[Final deployment snapshot](2026-11-05-regional-write-final-deployed.json).

Each repeat used 40 measured sequential writes per mode and five excluded
warmups through a reused HTTPS connection from SJC. Every measured response ID
matched its exact origin request-log observation. Every database contained
exactly 40 rows per measured mode plus 15 warmups at GRU, then returned 204 on
deletion. No completed repeat or latency outlier is excluded. With 40 samples,
the reported p99 is the observed maximum; this is a warmed sequential latency
proof, not a saturation or long-duration availability test.

| Repeat | Mode | Server p50 ms | Server p99 ms | Client p50 ms | Client p99 ms |
| --- | --- | ---: | ---: | ---: | ---: |
| 1 | default | 14.38 | 50.30 | 16.80 | 52.22 |
| 1 | durable_home | 229.58 | 267.27 | 231.59 | 270.52 |
| 1 | forced_home | 205.22 | 240.62 | 207.70 | 242.79 |
| 2 | default | 11.86 | 59.94 | 14.00 | 62.06 |
| 2 | durable_home | 227.90 | 250.81 | 230.08 | 252.65 |
| 2 | forced_home | 203.50 | 232.59 | 205.49 | 235.01 |
| 3 | default | 12.03 | 63.87 | 14.43 | 69.35 |
| 3 | durable_home | 227.37 | 342.92 | 229.80 | 345.40 |
| 3 | forced_home | 201.80 | 407.40 | 203.91 | 409.76 |

[All final samples, paired stages, cleanup and six-node snapshots](2026-11-05-regional-write-final-sjc.json).

The comparable before/after ranges below cover three measured repeats per
cohort. Server time uses paired request-log observations in both cohorts.
The earlier baseline was `d38f9b02e`; default writes then committed at home.
The final cohort uses two-node regional acknowledgements. The intermediate
post-HLC diagnostic's home-durable p99 reached 617.77 ms and included the two
initial-lease retries per repeat; the final cohort has zero such retries.

| SJC metric | Before, three repeats ms | Final, three repeats ms | Target ms |
| --- | ---: | ---: | ---: |
| Default server p50 | 196.06–205.66 | 11.86–14.38 | <20 |
| Default server p99 | 225.90–373.12 | 50.30–63.87 | — |
| Default client p50 | 197.93–207.82 | 14.00–16.80 | — |
| Default client p99 | 227.73–374.99 | 52.22–69.35 | <150 |
| Home-durable server p50 | 222.37–223.42 | 227.37–229.58 | — |
| Home-durable server p99 | 262.12–267.35 | 250.81–342.92 | — |
| Home-durable client p50 | 224.27–225.32 | 229.80–231.59 | ≈one RTT plus apply |
| Home-durable client p99 | 264.36–269.65 | 252.65–345.40 | <400 |

Default acknowledgement medians were 9.05–10.31 ms, including a 5.46–6.04 ms
regional durable hold and 2.61–2.97 ms local commit. Home-durable home-wait
medians were 190.69–191.11 ms, close to the 187.86–187.99 ms mesh RTT. GRU
made exactly 90 apply attempts per repeat, without retries; their mean durations
were 12.25, 11.75 and 12.91 ms. The old 100+200 ms retry penalty is absent.
The final worst home-durable sample spent 310.88 ms in home wait and still
completed in 345.40 ms client time.

DECISION: ship the local-ack fixes and retain the forced-home 409.76 ms p99
against the 400 ms comparison as a shared-pool follow-up in
[#1103](https://github.com/TerraScale/terrascale/issues/1103). Reason: default
local-ack and opt-in home-durable meet their stated targets; the forced-home
outlier contains an extra WAN RTT in the shared HTTP forwarding pool. Preserve
durability and the read path while tracking a write-pool correction separately.

The measured modes are distinct: `durable_home` sends `TerraScale-Durability:
home` and has worst client p99 **345.40 ms**; `forced_home` sends
`TerraScale-Write: home` and has worst client p99 **409.76 ms**. The latter's
home span was 385.80 ms with only 10.20 ms inside GRU's application span,
leaving 375.60 ms outside that application window, approximately two mesh
RTTs. The named shared client uses IHttpClientFactory's default two-minute
handler lifetime. Handler retirement and replacement connection setup are the
leading explanation; no per-connection trace was captured. The issue retains
the exact request ID, UTC timestamp and stage evidence.

## FRA diagnostic and cleanup boundary

A FRA repeat on `80303f9d3` measured the following rows before database deletion
returned 503 with key destruction pending. It has no final snapshot and is
ineligible for the three-repeat SJC Done proof. Further FRA repeats were held
while custody cleanup was pending, following the parent's instruction to record
the accepted transport follow-up and finish this task. FRA's numbers are
retained as diagnostics and do not establish the SJC latency thresholds there.

| Mode | Server p50/p99 ms | Client p50/p99 ms |
| --- | ---: | ---: |
| default | 37.70 / 182.67 | 41.59 / 187.90 |
| durable_home | 229.82 / 488.03 | 232.69 / 492.16 |
| forced_home | 195.78 / 223.26 | 198.92 / 228.16 |

[Raw FRA diagnostic and cleanup status](2026-11-05-regional-write-final-fra-diagnostic.json).
Its database ID is `01M6PAK5M31V83QZJD9N6M7DQQ`.

Cleanup completed: `fastwrites-sjc-proof-20261105`,
`fastwrites-fra-proof-20261105`, `fastwrites-sjc-window2-20261105` and
`fastwrites-fra-window2-20261105` all returned `DELETED`. The FRA account's
successful deletion at 17:09 UTC completed its pending database cleanup; no
account or proof resource remains outstanding. Private key/state copies were
removed from both serving hosts, and local helper files are absent.
[Cleanup confirmation](2026-11-05-regional-write-cleanup.json).
The earlier pending SJC case was also resolved and handed back to F11.
