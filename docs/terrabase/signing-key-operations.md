# Signing-key recovery

TerraBase validates ts-auth tokens using the last nonempty public key set from its configured issuer. The snapshot
is stored at `<data-directory>/auth-signing-keys.json`, with issuer, version and original successful-fetch time.
Writes flush a sibling `.new` file and atomically rename it over the snapshot. Keep this file with the node’s data
directory across restarts; it contains public verification material only. It is trusted local state: restrict write
access to the service account as for the rest of the data directory. Never replace it with keys from another issuer.

The maximum snapshot age is 24 hours, including time spent stopped. Restart does not renew it. A successful refresh
replaces the whole set; a failed refresh or empty JWKS retains it without renewing its timestamp. A removed issuer key
continues verifying until a successful refresh, normally within ten minutes plus a ten-second fetch deadline, and
potentially until the 24-hour maximum during an outage. Token expiration and API-key status admission still apply.

An expired, empty, malformed, future-dated or wrong-issuer snapshot cannot authorize requests. Without usable keys,
HTTP token requests return `503 auth_unavailable` and `Retry-After: 1`; SDKs retry that response. Unknown key IDs also
return this response when their refresh fails. Invalid signatures under held keys still return 401. Readiness remains
independent of issuer availability so the issuer can recover its own database.

Without usable keys the node retries forever, two seconds after each attempt, each bounded to ten seconds. When the
issuer becomes healthy, the current attempt and one following attempt take at most 22 seconds to fetch usable keys.
Unknown-key refreshes share in-flight work and admit at most one request-triggered refresh per thirty seconds;
allow forty seconds after issuer recovery for an unknown key to refresh. Known keys validate throughout an outage
while their snapshot remains younger than 24 hours.

## Metrics and alerts

Scrape the node’s administrative metrics listener. These gauges have no token, project or key-ID labels:

| Prometheus metric | Meaning | Alert |
| --- | --- | --- |
| `terrabase_auth_signing_keys_loaded` | 1 for a usable snapshot, otherwise 0 | Equal to 0 for one minute |
| `terrabase_auth_signing_keys_age_seconds` | Seconds since last successful fetch; -1 before any snapshot loads | Above 1200 for five minutes |
| `terrabase_auth_key_fetches_total{outcome="failure"}` | Failed discovery, JWKS or unusable-key-set fetches | Investigate alongside increasing age |

The age warning leaves more than 23 hours before validation stops. Check discovery and JWKS availability from the
node, the issuer’s storage health, and snapshot persistence warnings. After recovery, verify that loaded is 1 and age
falls below 600 seconds. A `Loaded … stored ts-auth public signing keys` log proves startup used the stored set.
Do not delete the snapshot to clear an alert: that removes restart protection without repairing issuer access.

Project mapping misses are never retained in the mapping cache. Key-status cache refusals require explicit
`key_revoked` or `key_expired` answers after ts-auth’s authoritative read; unavailable reads and generic invalid-token
responses never become cached revocations.
