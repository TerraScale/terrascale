# Database regions

A database selects the regions that hold it. TerraBase keeps one writer and synchronizes the other selected regions. Regions that hold the database serve supported reads locally; writes follow the [SQL router](sql/write-routing.md). Change placement with the [regions API](placement.md).

## Requests outside placement

An SDK needs no knowledge of a database's region. A region that does not hold the database authenticates the
customer locally, applies the organization's request admission, then proxies to a region that holds it. The
destination authenticates the same credential and applies its own request admission before executing work.
A proxied request therefore passes two admissions and pays one extra inter-region round trip, about 170 ms
between the current regions. Only the destination meters requests, operation units and bytes.

### Forwarding between regions

REST forwards on the authenticated internal listener, without a client redirect. The proxy carries the remaining
request time as a duration and enforces that budget across discovery, connection, sending and the complete
response. It never retries an unanswered request. Once a write body has been sent, a failed leg answers
`write_outcome_unknown`, because the destination may have committed it.

SQL WebSocket browser credentials arrive in the first frame. The receiving region accepts the upgrade and reads
at most 16 KiB within five seconds, checks the credential locally, and only then discovers and connects to the
destination. It replays the frame so the destination authenticates independently. Invalid or absent credentials
produce the same local error and close as a session for a database that region holds. Header credentials are
checked before the upgrade is forwarded. Every later request passes admission at both regions.

PostgreSQL validates the password or exchanged project credential locally before relaying its startup and password.
The destination validates that credential again. Statements pass request admission at both ends. Session caps
attach separately where the SQL WebSocket and PostgreSQL relays open their sessions.

Each attempt crosses at most one inter-region hop. A destination that does not hold the database refuses a second
hop before executing work. On that explicit refusal the receiving region invalidates its cached destination,
refreshes discovery once, and tries once more only if the region changed. Both attempts share the original
deadline. Forced refresh is limited to once per database per second; no different destination means retryable
503 `unavailable` with `Retry-After: 1`.

A writer move closes pinned WebSocket sessions with 1012 and PostgreSQL sessions with FATAL 57P01. An open
transaction is rolled back. A dropped proxy leg also rolls back an open transaction unless its commit was already
in flight: that commit's outcome is unknown. REST/WebSocket reports `write_outcome_unknown` when it can still
answer, and PostgreSQL reports SQLSTATE 08007. Reconnect and inspect committed state before deciding to send
a new write.

The forwarding marker is accepted only with the peer credential on the internal listener. Client-provided
markers are stripped and cannot authorize a second hop.

## Region discovery

Serving regions come from `TS_REGION` and the ts-orch peers file `TS_PEERS_FILE` ([ts-orch.md](ts-orch.md)). Peers
are used for routing and private listener binding; discovering a region grants no placement and creates no copy.

