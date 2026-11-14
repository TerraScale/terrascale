# Database proxy behavior in source

This page describes the unreleased server contract in this source revision. It is not a claim about published
SDK packages or the live service. The dashboard's SDK pins remain recorded in
[`sdk-versions.json`](../src/TerraScale.Dashboard/sdk-versions.json); published-package and server qualification follow release.

A database request reaching a region that does not hold the database is served through one proxy hop by a region
that does. REST keeps the method, body, query and credential. SQL WebSockets support both header credentials and
first-frame credentials. Both ends authenticate and enforce scope and database access; the serving region meters
the operation once. Internal replication and partner routes do not use this proxy.

Use the global API origin. A region pin selects the entry region but has no effect on the serving region for these
calls: the proxy strips it. The public SDK options remain available. A matching regional host and pin still need
to identify the same entry region; invalid pins are refused before database routing.

An SDK needs no knowledge of a database's region. A request to a region that does not hold the database adds
one inter-region round trip, about 170 ms between the current regions, and passes two request admissions:
the receiving region protects forwarding resources and the destination protects execution resources. Only the
destination records requests, operation units and bytes. Session caps attach separately when a relay opens its session.

Each attempt crosses at most one proxy hop. An explicit destination refusal because it does not hold the database
invalidates the cached route and permits one fresh discovery, rate-limited to once per database per second. A
second attempt is allowed only when the refreshed region differs, within the original deadline. An unchanged or
missing destination returns `503 unavailable` with `Retry-After: 1`. An unknown writer or unreachable destination
gives the same refusal without an internal host.
A REST request that reaches the old writer's fence after admission also returns `503 unavailable` with `Retry-After: 1`.
Discovery, connection, sending and the complete answer share a five-second total request budget. The proxy
carries the remaining duration to the destination and enforces the same remaining bound locally. A write already sent without an answer can return
`503 write_outcome_unknown`; reconcile by immutable identity before resending it.

A writer move closes sessions on the old writer: SQL WebSockets use 1012 and PostgreSQL uses FATAL 57P01.
Reconnect through the same origin to reach the new writer. The closed session's open transaction is rolled back.
If the proxy leg drops while a statement or commit is in flight, its outcome may be unknown: REST/WebSocket uses
`write_outcome_unknown` and PostgreSQL uses SQLSTATE 08007 when the proxy can still answer. Inspect committed
state before sending another write; the proxy never silently replays an unanswered request.
A relayed SQL WebSocket or PostgreSQL session takes one receiving-region request admission per busy period: the
first request sent while no answer is outstanding takes it, and the last answer returns it. A refused request is
answered `tenant_busy` (SQLSTATE 53300) and never reaches the destination. The relay never cuts a request the
destination is still serving; it treats the destination as lost when an HTTP/2 keep-alive ping goes unanswered.
