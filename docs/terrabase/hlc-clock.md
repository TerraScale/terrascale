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

For a fixed-width column stamp, allocate `HlcTimestamp.EncodedLength` (75) bytes
and call `stamp.WriteTo(bytes)`. `HlcTimestamp.ReadFrom(bytes)` reads exactly
75 bytes. Bytes 0–7 contain nonnegative Unix milliseconds as a big-endian signed
64-bit integer, bytes 8–11 contain the big-endian unsigned 32-bit logical counter,
and bytes 12–74 contain the complete ASCII node ID, padded with zero bytes.
Node IDs follow the existing 1–63-character lowercase letter/digit/hyphen rule.
This encoding preserves the full node tie-breaker without hashing or truncation;
unsigned bytewise comparison has the same order as `HlcTimestamp.CompareTo`.
Decoding rejects invalid lengths, timestamps, node IDs and nonzero padding.

`ILocalWriteClock.Allowed` is the R4 routing fact: false means forward the whole
transaction to home instead of taking a local write. `SkewMilliseconds` is the
current absolute local clock offset; positive infinity means unknown or stale.
`ClockSkewException` means timestamp issuance must be refused or forwarded home.

The implementation must durably advance before returning a stamp, preserve
monotonic order across restarts and backward wall-clock changes, refuse unknown
or over-50-ms local skew, and export the node's skew and admission state. These
contracts land separately so R6 and R4 can compile against the same source.

`NodeHlcClock` is the singleton timestamp issuer. A semaphore serializes issuance,
observation and initialization. It loads the versioned `node_clocks` record once
and advances timestamps in memory. Before issuing beyond its durable reservation,
it commits an upper bound five seconds ahead, with the maximum logical counter,
using a durable system-database transaction. A failed reservation returns no
stamp. All returned stamps are covered by an already durable upper bound;
concurrent issuers and accepted remote observations preserve HLC ordering.
This removes per-stamp system commits and their catalog-cache invalidations.

On restart, the persisted upper bound becomes the previous timestamp. Every new
stamp exceeds all stamps the former process could have issued, including after
an abrupt stop. The guard includes that recovered bound in its high-water mark:
local writes stay closed until UTC is within the existing 50 ms limit. A normal
restart can therefore delay local admission by up to five seconds; requests can
still forward home. A larger backward clock jump remains closed until recovery.
Invalid persisted state fails closed. Within the running process, admission
tracks issued and observed time, not the unused reservation. Observation of a
future stamp still closes admission immediately.

Local capture and the internal `Next` method retain their checks before and after
issuance. The router still forwards refused local transactions home. Home apply
can observe accepted remote timestamps without weakening the skew check.

The guard samples every five seconds. Each pass ensures persisted state was
loaded without issuing a stamp, so sampling an untrusted clock cannot persist a
false future timestamp. Once initialized it reads the shared in-memory high-water
mark. Unknown/stale time and the 50 ms admission limit remain unchanged.
It reads the local chrony daemon's tracking report over UDP on `127.0.0.1:323`,
using the same version-6 monitoring protocol and signed `current_correction`
field as `chronyc tracking`'s System time value. No chronyc executable or daemon
socket mount is needed inside the container; it must share the host's network.
The daemon reports NTP-minus-system time, so the guard subtracts this correction
from the application's offset relative to system UTC, then takes the absolute
value. This also detects an injected application clock offset.
Missing, malformed, unsynchronized or timed-out tracking replies fail closed.
Peer round-trip time and WAN reachability never enter local write admission.
Samples expire after 15 seconds, measured with monotonic time. Admission also
compares the current wall clock with elapsed monotonic time on every read, so a
clock jump immediately closes admission between samples.

Production TerraBase hosts need a synchronized chrony daemon with its loopback
monitoring port available, provisioned by ts-orch. The runtime does not change
the clock or require clock-setting privileges. `IClockErrorSource` isolates the
tracking measurement. The test binary replaces it with a fake source, so Fast
does not require chrony or NTP access on dev hosts.

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
if JetStream or the report bridge is unavailable.

The Slow proof contains two integration tests over the real gRPC listener and
real Turso storage. The happy path admits a healthy clock despite an unreachable
WAN peer and proves concurrent issuance and remote observation use the durable reservation
without rewriting it, then preserves stamp order across a backward-clock restart. The critical
path refuses a local-write stamp at an injected 100 ms offset.
R4 owns forwarding the entire refused transaction to home; R6 owns column LWW
and must compare the original stamp rather than mint a new one on receipt.
