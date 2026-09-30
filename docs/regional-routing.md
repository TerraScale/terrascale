# Regional routing

The default API base and discovery outage fallback are
`https://api.terrascale.tech`, the Atlas API proxy. Scoped discovery at
`discovery_base_url` (default `https://atlas.terrascale.tech`) returns the existing
native contract; the SDK does not add wire fields or translate UUID scopes into
database names. Atlas ranks measured RTT, client distance, and placement order.
Eligible regions use lowercased `https://<region>.api.terrascale.tech` origins.

The first eligible ranked endpoint becomes the pin. Pins last for the lesser of
`refreshSeconds` and remaining publication validity, using a conservative
monotonic lease. Refresh selects from the new ranking. A transport failure,
5xx, or 421 excludes that region for the document's remaining lifetime and
selects the next eligible region. Reads, queries, subscriptions, and transaction
status may fail over with the default retry budget. Commits remain single-send;
an ambiguous result retains the prepared transaction for explicit reconciliation.
Authentication and deterministic failures do not trigger routing failover.

Unavailable, malformed, or timed-out discovery uses the Atlas API proxy and backs off for
one second. `discovery_timeout` defaults to three seconds and is bounded at
30 seconds. Caller cancellation by dropping its future stops owned work.
Local scope, credential, capability, clock, and observation-floor errors continue to
fail validation. Native credentials can only reach canonical Atlas discovery.

`preferred_region` with `region_affinity(RegionAffinity::Failover)` selects an
eligible preferred region before discovery order. `RegionAffinity::Strict`
requires a preferred region and selects its `.api.` host directly. A custom
`api_base_url` disables discovery, including with `discovery_base_url` set.

The migrated fixed-clock endpoint fixtures are derived codec evidence. They do
not represent newly captured Worker responses or qualify the deployed native
receiver. Atlas's database-name ADR 0050 document and this SDK's separately
merged native UUID contract remain different server operations to integrate.
