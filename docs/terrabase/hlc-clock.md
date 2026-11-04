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
router; home apply can still observe accepted remote timestamps.
