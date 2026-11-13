# HLC and clock admission (PIVOT R12 / D21)

`TerraBase.Features.Clock.IHlcClock` is the timestamp source shared by local
capture and R6 home apply. `IssueAsync(CancellationToken)` issues a timestamp for a local write (see below);
`ObserveAsync(HlcTimestamp, CancellationToken)` incorporates remote
time, limiting its contribution to local UTC plus 50 ms. `ObserveAsync` returns `ValueTask<HlcTimestamp>`; `IssueAsync`
returns `ValueTask<HlcTimestamp?>`, which is null when it refuses.

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
home; a commit at the home answers `409 serialization_conflict` with `Retry-After: 1` and commits nothing. Nothing
happened, so the request is always safe to send again; every SDK resends that code on SQL execute. No caller reads the trust state
itself. `terrabase_clock_stamps_total{outcome="issued"|"refused"}` counts the answers.

`NodeHlcClock` is the singleton timestamp issuer. A semaphore serializes issuance,
observation and initialization. It loads the versioned `node_clocks` record once
and advances timestamps in memory. Before issuing beyond its durable reservation,
it commits an upper bound five seconds ahead, with the maximum logical counter,
using a durable system-database transaction. A failed reservation throws and issues no
stamp. All returned stamps are covered by an already durable upper bound;
concurrent issuers preserve HLC ordering.

Issuance computes its candidate from the previous HLC and a UTC read, then checks trust and the UTC plus 50 ms
bound before saving a reservation or advancing the HLC. A refused candidate changes neither the previous stamp,
reservation, high-water mark nor durable `node_clocks` record, so a forward step during issuance does not delay
recovery after correction, including across a restart.

Observing a received stamp takes the larger of the previous HLC and the received stamp capped at local UTC plus 50 ms.
Local UTC itself is never a candidate when observing: only `IssueAsync`, which runs only while the clock is trusted,
brings the node's own wall clock into the HLC. A node whose clock is wrong (closed, ahead) that keeps applying received
writes therefore never carries its wrong wall time into its HLC or onto disk, and issues again as soon as it reopens
after the clock is corrected. A received stamp beyond the cap is clipped for the observation only: the transaction is
applied with the stamp it carries, a warning is logged and `terrabase_clock_observe_capped_total` counts it.
This removes per-stamp system commits and their catalog-cache invalidations.

A clean shutdown replaces the reservation with the exact last stamp, so the next process resumes from it and issues
at once. After an abrupt stop, the persisted upper bound becomes the previous timestamp: every new stamp exceeds all
stamps the former process could have issued, and until the wall clock passes that bound minus 50 ms `IssueAsync`
refuses, so a crash within five seconds of the last stamp waits for its own reservation. Invalid persisted state fails
closed. Remote observations
cannot introduce an arbitrarily distant high-water mark. A warning and `terrabase_clock_observe_capped_total` counter
identify clipped observations; `terrabase_clock_hlc_ahead_milliseconds` reports the current high-water distance from
UTC once the HLC has been loaded.

Apply observes remote time within the bound and does not reject a transaction because of its timestamp. Applying a
received transaction uses the stamps it carries, so it keeps working on a node whose clock is not trusted.

## Clock trust

A node's clock is trusted when three checks hold. Closing is immediate on the first failing check, with two
exceptions for a node that is already open: the kernel check must fail for 60 s in a row (a time-daemon restart makes
the kernel report unsynchronised for a moment), and losing contact with every peer does not close it (`peers_lost`,
below). Opening after start, after a step, or after any untrusted period needs the kernel check to hold at once and two
consecutive agreeing peer rounds, both begun after the node closed. A measurement taken before the close never counts.

| Check | What it catches | How |
| --- | --- | --- |
| Kernel | small drift, an unsynchronised clock | Linux `adjtimex` (read-only) reports the clock synchronised and its estimated error at most 50 ms, on every read. The maximum error is not used: the kernel adds 500 µs to it every second between time-daemon updates, so it passes 50 ms on a healthy clock after about 100 s. |
| Peers | gross errors the kernel cannot see: a clock stepped by minutes, a virtual machine resumed with a stale clock, a restart with a wrong clock | Each round asks every peer for its wall clock (below). Either at least one peer answered in the last 15 s and the agreeing peers plus this node are a strict majority of the answering peers plus this node, or no peer answered in the last 15 s but at least one peer of the previous build did (first of its build, below). An open node that hears from no peer for 15 s stays open (`peers_lost`). |
| No local step | a step between two reads | Between consecutive checks, the wall clock and the monotonic clock advanced by the same amount within 50 ms. A step closes the node at once and latches: only rounds begun after it can reopen it. |

Peer rounds run every second. A round asks the in-region partners every time and the other regions' nodes every fifth
round; while the node is closed, first of its build or in `peers_lost`, it asks every peer each round. The peers are every other node of
the peers file. A request is `GET /v1/sync/clock` on the peer's internal listener with the peer credential; the peer
answers its wall clock in Unix milliseconds. Each request has one of three outcomes:

| Outcome | What the peer answered | Effect |
| --- | --- | --- |
| Answered | `200` with its wall clock | Counts as agreeing or disagreeing. The node records the wall clock when it sends and the monotonic round trip: offset is the peer's time minus the send time plus half the round trip, and uncertainty is half the round trip plus 1 ms. The peer agrees when the absolute offset is at most 50 ms plus that uncertainty. |
| Does not know the route | `404` with TerraBase's JSON error body and code `unknown_route` | The peer runs a previous build. It is reachable and accepted the peer credential, but it has no clock to compare. |
| No contact | anything else: no answer within 2 s, a refused connection, `401`, any other status, a `404` without that body | Nothing. The peer's last answer stays until it is 15 s old. |

A peer's answer counts for 15 s from the round that received it: a failed request does not remove it, so a network
blip in one round changes nothing. A fresh answer always replaces the previous one, so a peer that answers and
disagrees takes effect in the round that hears it.

Only a TerraBase internal listener that holds the same peer secret can answer "does not know the route", as far as
the node can tell. The request goes over HTTP/2 with prior knowledge (no upgrade, no proxy, no redirect) to the address
the peers file gives for that peer and to the internal port, so a client-listener port (HTTP/1 only), a web server or
an HTTP proxy on a wrong address fails the connection. The internal listener checks the peer credential before it
routes: a wrong or missing credential answers `401`, never `404`. Only after the credential matched does an unknown
path reach the status-code page, which answers `application/json` with `{"error":{"code":"unknown_route",...}}`; the
node accepts exactly that, and counts a bare `404`, a `404` of another content type or with another code as no contact.
The answer itself is not signed: a process that listens on the peer's internal address and imitates that body would
pass. Such a process already sits on the cluster's private network, where it could do more harm in other ways.

A node with no peers file has nobody to compare with and no replication to protect; only the kernel and step checks
apply.

## First of its build

During a rollout, the first node that runs a build with the clock route has no peer that answers it: every other node
still runs the previous build and answers `404 unknown_route`. Waiting for a second node would make that node, as the
home of its databases, answer `503` to every write until the next node is upgraded. So when no peer answered in the
last 15 s but at least one peer of the previous build did, the peer check holds and the node relies on the kernel and
step checks alone, exactly as a node with no peers file does. Its mode is `first_of_build`.

- The rule to open still applies: two consecutive rounds, both begun after the last close. In this mode a round counts
  when the kernel is good and at least one peer answered `404 unknown_route` in a round begun after the close. A step
  closes the node and latches as always.
- A peer that answers the route always outranks peers of the previous build: as soon as one peer answers, the majority
  rule decides. When that peer disagrees, the node closes at the end of that round, even while other peers still
  answer `404`.
- A node that opened as first of its build and then hears from no peer at all for 15 s moves to `peers_lost` and
  stays open, like a node that lost its agreeing peers. A node that is closed and isolated stays closed.

What it catches: an unsynchronised kernel, a kernel error estimate over 50 ms and a local step, all as on any node.
What it does not catch: a clock that is far off while the kernel still reports it synchronised with a small error (a
time daemon that follows a wrong source, a virtual machine resumed with a stale clock before the kernel notices). That
window lasts only until a second node runs the build: from then on the two compare clocks every round.

## Peers lost

A home commit does not depend on replication, and losing the network does not make a clock wrong: the kernel and
step checks still hold. So a node that is open (`peers_agree` or `first_of_build`) and then has no answering peer for
15 s keeps issuing stamps in the mode `peers_lost` for as long as the kernel and step checks hold. A region with one
working database node keeps committing writes to its homed databases through an inter-region outage.

- It returns to `peers_agree` when a peer answers and agrees (or to `first_of_build` when only peers of the previous
  build answer).
- It closes at once when a peer answers and disagrees, on a step, or when the kernel check fails for 60 s.
- Isolation never opens a node: from `closed`, opening still needs agreeing peers, the first-of-its-build rule or no
  peers file. It only keeps open a node that was trusted when contact was lost.

What it cannot catch: a clock that goes wrong slowly while the kernel still reports it synchronised with a small error,
during the isolation. `terrabase_clock_mode{mode="peers_lost"}` shows every node in that state.

## Kernel grace

Restarting the time daemon (an unattended package upgrade does this, possibly on every host at once) makes the kernel
report the clock unsynchronised for a moment. While a node is open and the step check holds, the kernel's
unsynchronised flag or an estimated error over 50 ms must persist for 60 s before it closes the node; a clock cannot
drift meaningfully in that time. At start and from `closed`, the kernel check must hold at once: there is no grace for
opening.

Consequences:

- A healthy node opens about two seconds after its process starts (two rounds: in three journey runs it was trusted 53 to 374 ms after `/readyz`, 1.9 to 3.3 s after a restart began), instead of waiting minutes for the time daemon.
- One node with a wrong clock among three closes alone: the other two each count one agreeing peer plus themselves.
- Two nodes that disagree with nobody else answering both close; neither can tell which clock is wrong.
- A rollout refuses no writes by itself. The first node upgraded relies on its kernel and step checks until a second
  node runs the build (first of its build, above); from then on both compare clocks. Every node should reach
  `peers_agree` within seconds of the second node being upgraded; every upgraded node does. A node still in `first_of_build` after the rollout
  means that every peer that answers it still runs an older build.
- The model cannot detect every node being wrong by the same amount (the peers then agree with each other); a drift
  below 50 ms plus the round-trip uncertainty that the kernel does not report; or a step that the wall/monotonic check
  misses because the host moved both clocks together, until the next peer round.

| State or event | Trusted | Time to change |
| --- | --- | --- |
| Start | no | opens after two agreeing rounds, about two seconds |
| Kernel unsynchronised or error over 50 ms, node open | yes, for 60 s | closes on the first read after 60 s in a row; a good read resets the 60 s |
| Kernel unsynchronised or error over 50 ms, node closed or starting | no | stays closed; opens two rounds after the kernel is good again |
| Step detected | no | closes on the read that sees it; reopens two rounds begun after the step |
| A round where answering peers do not agree | no | closes at the end of that round |
| No peer answered in the last 15 s, node open | yes | moves to `peers_lost` and stays open |
| No peer answered in the last 15 s, node closed | no | stays closed |
| No peer answers, at least one answers `404 unknown_route` | yes, after two such rounds | stays open; becomes `peers_agree` when a peer answers and agrees |
| No agreeing round for 15 s | no | closes on the next read |
| Two consecutive agreeing rounds after a close | yes | opens at the end of the second round |

`terrabase_clock_mode{mode,reason}` says which mode the node is in: it is one for the current pair and zero for the
others, with no per-peer label. One log line, `Clock trust mode {Mode}, reason {Reason}`, is written on each change.

| mode | reason | Stamps |
| --- | --- | --- |
| `peers_agree` | `none` | issued: a strict majority of the answering peers plus this node agree |
| `peers_lost` | `none` | issued: the node was open, no peer answered for 15 s; kernel and step checks hold |
| `first_of_build` | `none` | issued: no peer answers the route, at least one peer of the previous build is in contact |
| `no_peers` | `none` | issued: no peers file is configured |
| `closed` | `opening` | refused: the checks hold, fewer than two rounds since the last close |
| `closed` | `kernel` | refused: kernel unsynchronised or its estimated error over 50 ms (for 60 s when the node was open) |
| `closed` | `step` | refused: a local step, no round begun after it has finished |
| `closed` | `peers_disagree` | refused: peers answered and the majority disagrees |
| `closed` | `no_contact` | refused: the configured peers file lists no other nodes, or the node was not open and no peer answered or answered `404 unknown_route` in the last 15 s |
| `closed` | `stalled` | refused: no round finished for 15 s |

`terrabase_clock_local_writes_allowed` is one while the clock is trusted. `terrabase_clock_agreeing_rounds` counts the
consecutive agreeing rounds since the last close, `terrabase_clock_peers_answering` and
`terrabase_clock_peers_agreeing` count the answers of the last 15 s, `terrabase_clock_steps_total` counts detected
steps, `terrabase_clock_failed_rounds_total` counts rounds that ended with an unexpected error (one warning each; the
loop goes on, and a node whose rounds keep failing closes as `stalled` after 15 s), and
`terrabase_clock_skew_milliseconds` is the largest kernel error or agreeing-peer offset plus uncertainty (positive
infinity while untrusted). Every fifth round the node reports this to `terrabase.clock.report` over NATS; ts-orch
stores the reports in `tb-clock-skew` with three replicas and a 15-second TTL, and `terrabase_clock_reported` is one
when the last report was stored. Trust never depends on that report.

The runtime does not change the clock or require clock-setting privileges, and it needs no time-daemon socket.

Known limits: a kernel-synchronisation flag that alternates faster than the 60 s grace never closes the node on its
own; the peer check still runs every round.
A refused stamp answers with the retryable conflict code today, so a clock outage appears in
`terrabase_clock_stamps_total{outcome="refused"}` and the mode metric rather than a dedicated error code.

What to watch during a rollout:

- `terrabase_clock_mode` per node: every upgraded node in `peers_agree` (`first_of_build` only until a second node runs
  the build; `peers_lost` or `closed` needs a look).
- `terrabase_clock_stamps_total{outcome="refused"}` on the homes: it stays flat.
- `terrabase_clock_hlc_ahead_milliseconds` stays at or below 50 on every node.
- `terrabase_clock_peers_answering` and `terrabase_clock_peers_agreeing`: equal to each other and to the peers each
  node asks.

## Mixed builds

Nothing new is required of a node on the previous build. Asked for the clock route, it answers `404 unknown_route`
after checking the peer credential, as for any path it does not know. The stamps a new node issues have the same shape
and travel in the same `RegionTransaction`, so an old node applies them as before. A new replica whose clock is not
trusted forwards its writes home over the same forwarding path, and an old home stamps them as it always did. A new
home that receives a write forwarded by an old replica stamps it under its own trust state, like any write it commits.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.

R4 owns forwarding the entire refused transaction to home; R6 owns column conflict resolution
and must compare the original stamp rather than mint a new one on receipt.
