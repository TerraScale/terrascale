# Regional routing

Default native traffic uses authenticated scoped discovery at
`https://atlas.terrascale.tech/api/v1/discovery`. The SDK sends one canonical
native `tbk1` credential and the exact tenant/database UUIDs and full-u64 shard scope.
Atlas owns eligibility, residency, publication authority, and ranking. The SDK
selects the first receiver-ranked endpoint supporting each requested operation.
Direct native endpoints use `https://<region>.api.terrascale.tech`, such as
`https://gru.api.terrascale.tech`.

`RegionRtt` hints supply bounded measured ranking inputs through the admitted
header. They do not admit a region or override the receiver's ordered response.
There is no caller-selected region affinity or synthetic regional destination.
All valid discovered serving regions
participate automatically, subject to tenant admission and residency. Existing
direct `api_base_url` paths are restricted to reviewed isolated tests and real
producer qualification, not first-party node startup or region activation.
Inside the TerraScale zone it must be exactly `https://<region>.api.terrascale.tech`,
with a valid single region label. Reserved service labels, the apex, nested labels,
retired `<region>.terrascale.tech` origins, explicit ports and trailing dots are rejected before
credentials can be dispatched. Independent HTTPS and explicit loopback/test
origins are isolated qualification/test inputs requiring independent admission;
failed production discovery must never fall back to them.

Discovery transport failures, timeouts, malformed responses, denials, unavailable
eligibility, expired publications, and clock or observation regressions return
errors before native dispatch. The global `api.terrascale.tech` proxy is rejected
as a native origin, default, or fallback. Failed discovery never creates a bypass.

Scoped observations retain their authority and revision floors after lease
expiry. A conservative wall and monotonic lease is at most ten seconds and is
clamped down by expiry and request time. Safe native retries refresh admission
when needed; mutations are never automatically replayed. These client checks do
not establish hosted availability, healthy-clock bounds, or native runtime
qualification.

Each public native call has one 30-second operation budget. Initial admission,
same-scope resolver waits, discovery transport, native dispatch and any requested
safe retry delays share that deadline; neither a refreshed lease nor a retry
renews it. Direct public discovery also bounds the entire resolution. Dropping
or timing out a refresh releases its transport and reservation while retaining
any prior observation floor. A dispatched mutation that times out remains
uncertain and retains the prepared operation for explicit reconciliation.

Safe retries default to zero and are capped at eight when explicitly configured.
They select the current first eligible Atlas rank without demoting a failed
origin or pinning a different region. Discovery errors are propagated, with no
negative-cache bypass or automatic fallback. Deadline bounds do not suppress
separate rapidly failing calls; the SDK makes no failure-load suppression claim.
