# HLC and clock admission (PIVOT R12 / D21)

`TerraBase.Features.Clock.IHlcClock` is the timestamp source shared by local
capture and R6 home apply. `IssueAsync(CancellationToken)` issues a timestamp for a local write (see below);
`ObserveAsync(HlcTimestamp, CancellationToken)` incorporates remote
time, limiting its contribution to local UTC plus 50 ms. Both return `ValueTask<HlcTimestamp>`.

`HlcTimestamp(long PhysicalMilliseconds, uint Logical, string NodeId)` orders by
Unix milliseconds, logical counter, then ordinal node ID. R6 column conflict resolution compares
this entire tuple, including the node ID when two clocks issue the same physical
and logical values. Persist the entire tuple with each changed column. A replay
retains the original timestamp; receipt order never replaces it.

For a fixed-width column stamp, allocate `HlcTimestamp.EncodedLength` (75) bytes
and call `stamp.WriteTo(bytes)`, which writes exactly 75 bytes. Bytes 0–7 contain nonnegative Unix milliseconds as a big-endian signed
64-bit integer, bytes 8–11 contain the big-endian unsigned 32-bit logical counter,
and bytes 12–74 contain the complete ASCII node ID, padded with zero bytes.
Node IDs follow the existing 1–63-character lowercase letter/digit/hyphen rule.
This encoding preserves the full node tie-breaker without hashing or truncation;
unsigned bytewise comparison has the same order as `HlcTimestamp.CompareTo`.

`IssueAsync(CancellationToken)` is the only way to get a new stamp for a write that originates on this node. It
returns no stamp while the node's clock is not trusted (below) or while the node's own HLC is ahead of its wall clock
by more than 50 ms. Callers decide what a refusal means: a replica-local commit forwards the whole transaction to the
home; a commit at the home answers `503 unavailable` (retry) and commits nothing. No caller reads the trust state
itself. `terrabase_clock_stamps_total{outcome="issued"|"refused"}` counts the answers.

`NodeHlcClock` is the singleton timestamp issuer. A semaphore serializes issuance,
observation and initialization. It loads the versioned `node_clocks` record once
and advances timestamps in memory. Before issuing beyond its durable reservation,
it commits an upper bound five seconds ahead, with the maximum logical counter,
using a durable system-database transaction. A failed reservation returns no
stamp. All returned stamps are covered by an already durable upper bound;
concurrent issuers preserve HLC ordering. Remote timestamps beyond the observation bound are clipped for local issuance without changing the timestamp carried by their effects.
This removes per-stamp system commits and their catalog-cache invalidations.

On restart, the persisted upper bound becomes the previous timestamp. Every new
stamp exceeds all stamps the former process could have issued, including after
an abrupt stop. Until the wall clock passes that recovered bound minus 50 ms, `IssueAsync` refuses, so a restart within
five seconds of the last stamp waits for its own reservation. Invalid persisted state fails closed. Remote observations
cannot introduce an arbitrarily distant high-water mark. A warning and `terrabase_clock_observe_capped_total` counter
identify clipped observations; `terrabase_clock_hlc_ahead_milliseconds` reports the current high-water distance from
UTC once the HLC has been loaded.

Apply observes remote time within the bound and does not reject a transaction because of its timestamp. Applying a
received transaction uses the stamps it carries, so it keeps working on a node whose clock is not trusted.

## Clock trust

A node's clock is trusted when three checks hold. Closing is immediate on the first failing check; opening after
start, after a step, or after any untrusted period needs two consecutive agreeing peer rounds, both begun after the
node closed. A measurement taken before the close never counts.

| Check | What it catches | How |
| --- | --- | --- |
| Kernel | small drift, an unsynchronised clock | Linux `adjtimex` (read-only) reports the clock synchronised and its maximum and estimated error at most 50 ms, on every read. |
| Peers | gross errors the kernel cannot see: a clock stepped by minutes, a virtual machine resumed with a stale clock, a restart with a wrong clock | Each round asks every reachable peer for its wall clock (below). At least one peer answered in the last 15 s, and the agreeing peers plus this node are a strict majority of the answering peers plus this node. |
| No local step | a step between two reads | Between consecutive checks, the wall clock and the monotonic clock advanced by the same amount within 50 ms. A step closes the node at once and latches: only rounds begun after it can reopen it. |

Peer rounds run every second. A round asks the in-region partners every time and the other regions' nodes every fifth
round; while the node is closed it asks every peer each round. The peers are every other node of the peers file.
A request is `GET /v1/sync/clock` on the peer's internal listener with the peer credential; the peer answers its wall
clock in Unix milliseconds. The node records the wall clock when it sends and the monotonic round trip: offset is the
peer's time minus the send time plus half the round trip, and uncertainty is half the round trip plus 1 ms. A peer
agrees when the absolute offset is at most 50 ms plus that uncertainty. A peer that does not answer within 2 s, or
answers an error or `404` (an older build without the route), did not answer: it neither agrees nor disagrees. A node
with no peers file has nobody to compare with and no replication to protect; only the kernel and step checks apply.

Consequences:

- A healthy node opens about OPEN_AFTER_RESTART after it starts (two rounds), instead of waiting for the time daemon.
- One node with a wrong clock among three closes alone: the other two each count one agreeing peer plus themselves.
- Two nodes that disagree with nobody else answering both close; neither can tell which clock is wrong.
- During a rollout, the first node on this build has no answering peer: as a replica it forwards its writes home, and
  as a home it answers `503` for writes to its replicated databases, until a second node runs this build.
- The model cannot detect every node being wrong by the same amount (the peers then agree with each other); a drift
  below 50 ms plus the round-trip uncertainty that the kernel does not report; or a step that the wall/monotonic check
  misses because the host moved both clocks together, until the next peer round.

| State or event | Trusted | Time to change |
| --- | --- | --- |
| Start | no | opens after two agreeing rounds, about two seconds |
| Kernel unsynchronised or error over 50 ms | no | closes on the next read; reopens two rounds after the kernel is good again |
| Step detected | no | closes on the read that sees it; reopens two rounds begun after the step |
| A round where peers do not agree, or nobody answers | no | closes at the end of that round |
| No agreeing round for 15 s | no | closes on the next read |
| Two consecutive agreeing rounds after a close | yes | opens at the end of the second round |

`terrabase_clock_local_writes_allowed` is one while the clock is trusted. `terrabase_clock_agreeing_rounds` counts the
consecutive agreeing rounds since the last close, `terrabase_clock_peers_answering` and
`terrabase_clock_peers_agreeing` describe the last round, `terrabase_clock_steps_total` counts detected steps, and
`terrabase_clock_skew_milliseconds` is the largest kernel error or agreeing-peer offset plus uncertainty (positive
infinity while untrusted). Every fifth round the node reports this to `terrabase.clock.report` over NATS; ts-orch
stores the reports in `tb-clock-skew` with three replicas and a 15-second TTL, and `terrabase_clock_reported` is one
when the last report was stored. Trust never depends on that report.

The runtime does not change the clock or require clock-setting privileges, and it needs no time-daemon socket.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.

R4 owns forwarding the entire refused transaction to home; R6 owns column conflict resolution
and must compare the original stamp rather than mint a new one on receipt.
