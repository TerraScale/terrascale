# Hosted native routing

Both SDKs resolve the authenticated native8 document with
`GET https://atlas.terrascale.tech/api/v1/discovery`. The native key and exact
UUID tenant/database/shard scope are captured once for the complete operation.
Name-based ADR 0050 documents are not interpreted as native routes.

The canonical native origin is `https://<region>.api.terrascale.tech`, with the
region lowercased. The obsolete single-level regional hostname is rejected.
The SDK pins the lowest-numbered priority that supports the operation until the
earlier of `refreshSeconds` and `expires_at`. Discovery latency consumes the
lease; expiry, credential rotation and observation floors are checked before
sending to a region. No cookies or redirects are admitted.

A transport failure, HTTP 421, or 5xx excludes the failed region, re-resolves
Atlas, and selects the next eligible priority. The replacement pin lasts for
the refreshed lease. Normal refresh makes the preferred region eligible again.
Retries remain inside the original deadline and honor `Retry-After`. Reads,
queries, subscription snapshot/poll requests and transaction status may fail
over automatically; commits are never replayed automatically. A failed commit
changes routing for the next operation while preserving its frozen identity and
uncertain outcome. Use transaction status before an explicit retry.

When Atlas has a network error, times out, returns 404/5xx, or does not provide
a valid native8 success document, the SDK sends to `https://api.terrascale.tech`.
That Atlas Worker reverse-proxies to the nearest healthy region. The proxy is
used until the next discovery refresh, then a valid native8 document establishes
a regional pin. Before the first native document, the refresh interval is eight
seconds; afterward it uses the last native document's `refreshSeconds`.
Authentication/authorization errors, redirects, capability denial and regressed
observations remain errors. Proxy routing does not synthesize a native document
or capability grant; the server decides admission.

The hosted Atlas native8 producer is not yet published. It depends on
orchestrator directory work, so hosted callers currently use the proxy path.
Deterministic routing and transport fault tests prove SDK behavior. Archived
Atlas receiver captures and their hashes remain unchanged; host migration
fixtures derive the new origin without claiming a live producer receipt.
Live API qualification remains gated on real configuration.

An explicit endpoint disables discovery, including refresh and failover. Custom
HTTPS origins, canonical regional origins and the explicit Atlas proxy are
accepted; HTTP is limited to loopback development.

Go options: `DataBaseURL`, `DataDiscoveryTimeout`, `RequestTimeout`.
