# Hosted native routing

With `DataBaseURL` empty, Documents operations discover the authenticated native8
routing document through `GET https://atlas.terrascale.tech/api/v1/discovery`.
The exact UUID tenant/database/shard scope and a canonical native `tbk1` key are
captured for the complete operation. Product discovery preferences, static
catalogs, and name-based documents cannot provide native admission or affinity.

Atlas ranks eligible endpoints by proximity. For each operation the SDK selects
the first endpoint whose capabilities support that operation, even when a
previous operation selected another endpoint from the same cached document.
The canonical direct origin is `https://<region>.api.terrascale.tech`, with the
region lowercased. The global `api.terrascale.tech` host and retired regional
`<region>.terrascale.tech` origins are rejected.

Authenticated documents have bounded leases, scoped key fingerprints, and
monotonic deployment/cell/publisher/revision observations. The SDK accounts for
discovery latency and checks expiry and current observation floors before native
dispatch. Expiry and credential rotation cannot erase previously observed floors.
Native discovery and data requests do not send cookies or follow redirects.

`RequestTimeout` bounds the complete native operation (default and maximum
30 seconds). Atlas discovery and native dispatch share that original deadline;
a shorter caller deadline wins. Discovery deliberately has no separate
three-second cap, and native dispatch receives only the time remaining after
discovery. Choose a shorter request or caller deadline when the complete
operation needs a tighter budget.

Discovery denial, timeouts, transport errors, unavailable/404/5xx responses,
malformed documents, and missing capabilities stop the operation before data
dispatch. There is no API proxy, static catalog, fabricated affinity, or stale
lease fallback. Actual Atlas errors retain their HTTP status in `*sdk.APIError`,
including oversized or unreadable error responses. Complete error bodies of at
most 64 KiB can refine the status-based default code using recognized Atlas
status/code pairs. Producer messages, fixes, raw bodies, JSON correlation fields
and placement headers are discarded rather than redacted. A bounded ASCII
identifier from `X-Request-ID` and parsed `Retry-After` remain available; native
key syntax is rejected as a correlation identifier. Malformed, partial, or
oversized bodies retain the status-based default code. Reader errors are never
copied into these diagnostics. Local
discovery failures retain `*sdk.NativeRoutingError`, wrapped by `*sdk.RoutingError`.

A native transport failure or non-success response is returned to the caller.
No operation automatically replays against another region. A dispatched commit
may remain uncertain; its prepared transaction retains the frozen identity and
effects. Reconcile through transaction status before explicitly retrying.

An explicit `DataBaseURL` is a source-preparation or separately authorized
deployment option. It requires an ASCII HTTPS origin without credentials,
paths, query, fragment, or trailing-dot hostname. TerraScale origins must use a
lowercase direct regional hostname; the apex, reserved service labels, nested
service hosts, and retired regional origins are rejected. Independent HTTPS origins
remain valid for source preparation. HTTP is limited to loopback development.
An explicit-origin fixture does not qualify hosted runtime routing.

The archived Atlas success/denial captures are retained unchanged. Local caller
regressions derive finite routing fixtures from them; these checks do not claim
that a current public Atlas/region deployment is available.
