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

A request is never proxied twice. Stale placement at the destination returns `503 unavailable` with
`Retry-After: 1`. An unknown writer or unreachable destination gives the same refusal without an internal host.
REST connection establishment is bounded by five seconds. A write already sent without an answer can return
`503 write_outcome_unknown`; reconcile by immutable identity before resending it.

A writer move closes sessions on the old writer: SQL WebSockets use 1012 and PostgreSQL uses FATAL 57P01.
Reconnect through the same origin to reach the new writer. A fenced session never reports a retryable SQL conflict.
