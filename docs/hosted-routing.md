# Hosted native routing

With `DataBaseURL` empty, Documents operations discover the authenticated native8
routing document through `GET https://atlas.terrascale.tech/api/v1/discovery`.
The exact UUID tenant/database/shard scope and a canonical native `tbk1` key are
captured for the complete operation. Product discovery preferences, static
catalogs, and name-based documents cannot provide native admission or affinity.

Atlas ranks eligible endpoints by proximity. For each operation the SDK selects
the first endpoint whose capabilities support that operation, even when a
previous operation selected another endpoint from the same cached document.
The canonical direct origin is `https://<region>.terrascale.tech`, with the
region lowercased. The `api.terrascale.tech` host family is rejected.

Authenticated documents have bounded leases, scoped key fingerprints, and
monotonic deployment/cell/publisher/revision observations. The SDK accounts for
discovery latency and checks expiry and current observation floors before native
dispatch. Expiry and credential rotation cannot erase previously observed floors.
Native discovery and data requests do not send cookies or follow redirects.

Discovery denial, timeouts, transport errors, unavailable/404/5xx responses,
malformed documents, and missing capabilities stop the operation before data
dispatch. There is no API proxy, static catalog, fabricated affinity, or stale
lease fallback. Actual Atlas errors retain their status/code in `*sdk.APIError`;
local discovery failures retain `*sdk.NativeRoutingError`, wrapped by
`*sdk.RoutingError`.

A native transport failure or non-success response is returned to the caller.
No operation automatically replays against another region. A dispatched commit
may remain uncertain; its prepared transaction retains the frozen identity and
effects. Reconcile through transaction status before explicitly retrying.

An explicit `DataBaseURL` is a source-preparation or separately authorized
deployment option. It requires an ASCII HTTPS origin without credentials,
paths, query, fragment, or the API host family. HTTP is limited to loopback
development. An explicit-origin fixture does not qualify hosted runtime routing.

The archived Atlas success/denial captures are retained unchanged. Local caller
regressions derive finite routing fixtures from them; these checks do not claim
that a current public Atlas/region deployment is available.
