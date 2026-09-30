# Regional routing

Default native traffic uses authenticated scoped discovery at
`https://atlas.terrascale.tech/api/v1/discovery`. The SDK sends one canonical
native `tbk1` credential and the exact tenant/database UUIDs and full-u64 shard scope.
Atlas owns eligibility, residency, publication authority, and ranking. The SDK
selects the first receiver-ranked endpoint supporting each requested operation.
Direct native endpoints use `https://<region>.terrascale.tech`.

`RegionRtt` hints supply bounded measured ranking inputs through the admitted
header. They do not admit a region or override the receiver's ordered response.
There is no caller-selected region affinity or synthetic regional destination.
`atlas_base_url` defaults to canonical Atlas; a custom discovery origin fails
before native credentials are sent. An explicit reviewed direct `api_base_url`
remains available for producer qualification.

Discovery transport failures, timeouts, malformed responses, denials, unavailable
eligibility, expired publications, and clock or observation regressions return
errors before native dispatch. `api.terrascale.tech` is a test host and is rejected
as a native origin, default, or fallback. Failed discovery never creates a bypass.

Scoped observations retain their authority and revision floors after lease
expiry. A conservative wall and monotonic lease is at most ten seconds and is
clamped down by expiry and request time. Safe native retries refresh admission
when needed; mutations are never automatically replayed. These client checks do
not establish hosted availability, healthy-clock bounds, or native runtime
qualification.
