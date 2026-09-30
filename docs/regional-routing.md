# Regional routing

The default `BaseAddress` is `https://api.terrascale.tech`, the Atlas API proxy.
The first database-scoped request fetches the existing ADR 0050 discovery
response from `DiscoveryBaseAddress` (default `https://atlas.terrascale.tech`).
The SDK selects the top-ranked eligible endpoint and uses its lowercased
`https://<region>.api.terrascale.tech` origin directly.

Pins last for the document's `refreshSeconds`, including lifetimes below a
minute. Refresh selects from the new ranking. Transport failures, 5xx, and
421 demote failed regions; a safe or idempotency-keyed request can fail over
without setting `MaxRetryAttempts`. The selected alternative becomes the pin.
Failover is bounded by the available endpoints; a 421 placement repair runs
once. Authentication failures and ordinary 404 responses do not fail over.
Caller cancellation stops routing work, and excessive `Retry-After` delays
return the original response.

Unavailable, malformed, or timed-out discovery falls back to the Atlas API
proxy, with a short discovery failure backoff. Reactive attach uses the same
fallback. Database-less member routes select from the region catalog.

`PreferredRegion` with `RegionAffinity = Failover` selects an eligible preferred
region before falling back to discovery order. `RegionAffinity = Strict`
requires a preferred region and targets its `.api.` origin directly. A custom
`BaseAddress` disables discovery even with `DiscoveryBaseAddress` configured.
Each client owns its discovery, pin, and catalog state.
