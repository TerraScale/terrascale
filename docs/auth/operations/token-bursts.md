# A burst of token requests is answered at once

Every token request charges the caller's per-minute budgets before client authentication: the socket peer's
(`TS_AUTH_RATE_LIMIT`, default 1000) and, for an API key, the key's own (1000). All requests from one address share one
bucket row in `rate_limits`.

## What went wrong

A fixed window was charged by reading the bucket and then replacing it on the version it read. Concurrent requests on
one bucket lost that conditional write to each other: one won each round, the others read again, up to 64 rounds each.
A burst of 150 concurrent requests from one address made about 7,500 storage calls, 3,524 of them lost writes (TerraBase
answered `409`), and ts-auth's one HTTP/2 connection to TerraBase carried at most 100 of them at a time, so the rest waited
for a stream. Most requests were still looping when the token endpoint's 10 s deadline (`TokenEndpointDeadline`) ended
them with `503 temporarily_unavailable`, whether they were within the budget or over it.

## Now

- A fixed window is charged with one conditional increment (`UPDATE … SET used = used + 1 WHERE used < limit`, or a reset
  to 1 in a newer window). TerraBase applies concurrent increments one after another on the database's single writer, so
  requests on one budget never lose a write to each other. Cooldowns, token buckets and lockout-notice claims keep their
  version-checked replace.
- ts-auth opens further HTTP/2 connections to TerraBase when one connection's streams are all in use, as the dashboard's
  TerraBase client already does.

A request within the budget is admitted and goes on to client authentication; a request over it gets
`429 rate_limited` with `Retry-After` up to the window's end, without a write when the window is already used up. With
the budget lowered to 100 on the local stack, 150 concurrent requests answer 100 admitted and 50 `429`, where they
answered about 20 admitted and the rest `503` after 10 s before. When all of a burst arrives at the same moment, each
admitted request still costs one write on TerraBase's single writer, and an over-limit request's own increment waits
behind them, so the time to its `429` grows with the burst.
