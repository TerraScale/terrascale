# HLC and clock admission (PIVOT R12 / D21)

`TerraBase.Features.Clock.IHlcClock` is the timestamp source shared by local
capture and R6 home apply. `NextAsync(CancellationToken)` issues a timestamp;
`ObserveAsync(HlcTimestamp, CancellationToken)` advances past an accepted remote
stamp. Both return `ValueTask<HlcTimestamp>`.

`HlcTimestamp(long PhysicalMilliseconds, uint Logical, string NodeId)` orders by
Unix milliseconds, logical counter, then ordinal node ID. R6 column LWW compares
this entire tuple, including the node ID when two clocks issue the same physical
and logical values. Persist the entire tuple with each changed column. A replay
retains the original timestamp; receipt order never replaces it.

`ILocalWriteClock.Allowed` is the R4 routing fact: false means forward the whole
transaction to home instead of taking a local write. `SkewMilliseconds` is the
current conservative skew estimate; positive infinity means unknown or stale.
`ClockSkewException` means timestamp issuance must be refused or forwarded home.

The implementation must durably advance before returning a stamp, preserve
monotonic order across restarts and backward wall-clock changes, refuse unknown
or over-50-ms local skew, and export the node's skew and admission state. These
contracts land separately so R6 and R4 can compile against the same source.

`NodeHlcClock` is registered as the singleton `IHlcClock`. Each call reads and
advances its versioned high-water mark in the system database's `node_clocks`
collection, committing durably before returning. Durable write transactions
serialize concurrent issuers. Restarting with an earlier wall clock advances the
logical counter beyond the persisted stamp. Observing a remote stamp also
persists advancement before returning. Invalid persisted state fails closed.
Local write admission is checked separately through `ILocalWriteClock` by the
router. Local capture checks admission before issuing the stamp and again before
committing capture, so a clock jump during issuance forwards the whole transaction.
Home apply can still observe accepted remote timestamps.

The guard samples every five seconds. Each pass reads the persisted high-water
mark without issuing a stamp, so sampling an untrusted clock cannot persist a
false future timestamp. Issuance and observation update the shared in-memory
high-water mark after durable commit. Admission reads that current mark, so a
restart behind a previously issued stamp keeps local writes closed until wall
time recovers, and observing a future stamp closes admission immediately.
It reads Linux `adjtimex` without changing
the clock; an unsynchronized/error state fails closed. Peer samples use the
versioned `terrabase.clock.v1.NodeClock/Sample` gRPC service with LZ4 on the mesh. It
prefers in-region peers, falling back to all mesh peers while regions have only
one node. The request's monotonic round-trip gives an interval for peer offset;
the median interval across this node and reachable peers bounds skew without
letting one outlier disable a majority. A majority must respond. An interval
whose uncertainty reaches past 50 ms also refuses local writes; wide-area
latency can therefore cause conservative forwarding until a same-region peer
exists. Samples expire after 15 seconds, measured with monotonic time. Admission
also compares the current wall clock with elapsed monotonic time on every read,
so a clock jump immediately closes admission between samples.

The `Next` gRPC method issues a durable timestamp only when local admission is
open. Peer sampling itself is read-only and never advances the HLC. Both methods
are internal-listener only and require the existing peer credential. The mesh
listener's existing transport and authentication contract still applies.

Admin `/metrics` exports `terrabase_clock_skew_milliseconds`,
`terrabase_clock_local_writes_allowed` and `terrabase_clock_reported`.
Unknown skew is positive infinity; admission is zero. `reported` is one only
when the most recent NATS report was acknowledged as stored. Reports go to
`terrabase.clock.report`; ts-orch stores them in `tb-clock-skew` with three
replicas and a 15-second TTL. Admission continues to depend on local measurement
and mesh peers if JetStream or the report bridge is unavailable.

The Fast proof contains two integration tests over the real gRPC listener and
real Turso storage: restart with a backward clock preserves stamp order, and an
injected 100 ms offset against a mesh peer prevents issuing a local-write stamp.
R4 owns forwarding the entire refused transaction to home; R6 owns column LWW
and must compare the original stamp rather than mint a new one on receipt.
