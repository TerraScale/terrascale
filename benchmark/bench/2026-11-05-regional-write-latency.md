# Regional write latency: SJC client, GRU home

## Before local acknowledgements: exact server correlation

Serving source `d38f9b02e` includes the read owner's fixes. Three additional
measured repeats on SJC used 40 writes per mode through reused HTTPS. Every
sample's response `X-Request-ID` matched its SJC request-log observation, read
through the existing authenticated internal logs endpoint. No observation was
missing. The service's `latencyMs` measures the origin pipeline through response
completion, including admission and execution, without client TLS or the edge.
This supplies total server time before the new `Server-Timing` stages exist.

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

## Durability and scope

The baseline router's `LocalCommitAvailable` requires `homeDurable`, so an
ordinary request can be classified `route=local` while it commits at home.
The SQL endpoint also calls `CommitAsync(homeDurable: true)` unconditionally
on its local path. Together these make remote confirmation part of every
successful write, irrespective of the default local durability request.
The roughly 196–206 ms default server medians match this WAN dependency.

R10 owns enabling default local acknowledgements and the required durable
regional partner copy. N1 owns activating the second nodes. This work does not
weaken acknowledgement durability, change plan admission, or alter replica
reads. The diagnostic header and benchmark separate those dependencies from
other write-path cost.

## Qualification attempts

The first harness attempt exposed a local Python import error before any
database was created; it was corrected. One exploratory three-repeat run
completed its first repeat but received `400 sql_error` in the next repeat
while production moved from `e661a352b` to `6cacd06cf`. Its database cleanup
completed. A separate second-repeat qualification succeeded. These exploratory
runs are excluded from the measured table. The reported three repeats record
source stability and complete home-count validation.
