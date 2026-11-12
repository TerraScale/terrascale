# A burst of token requests is answered at once

A token request (`/terrascale/oauth/token`, `/terrascale/oauth/device`, a project's `oauth/token` and the end-user token
endpoint) is charged against these per-minute budgets, in this order:

1. The client's address: `TS_AUTH_RATE_LIMIT` requests (default 1000), an IPv6 address per /64. Behind ts-orch's edge it
   is the address the edge writes into `X-Forwarded-For` (`token-client-ip`). A peer that is not a trusted proxy is
   charged by its socket address (`token-peer-ip`), and its `X-Forwarded-For` is ignored.
2. For an IPv6 address, its /48 site, at four times that budget (the same purpose with `-site`).
3. For an API key, only once its secret matched, the key's own budget of 1000 (`api-key-token`, `end-user-token-key`).

Budgets 1 and 2 are charged before client authentication, so wrong secrets use them up; a wrong secret never reaches the
key's budget. ts-orch's edge reaches ts-auth on loopback, which ts-auth trusts as a proxy when `TS_NODE` is set, and it
writes the real client address, so the edge itself is not charged: one budget shared by every request through it would
let a single address that floods the endpoint refuse every other customer until the minute ends. A request that arrives
from the edge without a usable forwarded address is charged as the edge's own address, so all such requests share one
budget. Only a trusted proxy that is not on loopback (`TS_AUTH_TRUSTED_PROXIES`), which can put any address into the
header, is first charged its own aggregate budget of four times `TS_AUTH_RATE_LIMIT` (`token-proxy-ip`), then the
client's. All requests from one address share one bucket row per budget in `rate_limits`.

## What went wrong

A fixed window was charged by reading the bucket and then replacing it on the version it read. Concurrent requests on
one bucket lost that conditional write to each other: one won each round, the others read again, up to 64 rounds each.
A burst of 150 concurrent requests from one address made about 7,500 storage calls, 3,524 of them lost writes (TerraBase
answered `409`), and ts-auth's one HTTP/2 connection to TerraBase carried at most 100 of them at a time, so the rest waited
for a stream. Most requests were still looping when the token endpoint's 10 s deadline (`TokenEndpointDeadline`) ended
them with `503 temporarily_unavailable`, whether they were within the budget or over it.

## Now

- A fixed window is charged with one conditional increment of this region's count (`UPDATE … SET used = used + 1`, or a
  reset to 1 in a newer window), which applies only while this region's count plus the other two regions' counts for
  the window, summed in the same statement, is under the maximum. TerraBase applies concurrent increments one after
  another on the database's single writer at its home, so requests on one budget never lose a write to each other, and
  all regions together admit at most the maximum in a window. Cooldowns, token buckets and lockout-notice claims keep
  their version-checked replace.
- ts-auth opens further HTTP/2 connections to TerraBase when one connection's streams are all in use, as the dashboard's
  TerraBase client already does.

A request within the budget is admitted and goes on to client authentication; a request over it gets
`429 rate_limited` with `Retry-After` up to the window's end, without a write when the window is already used up. With
the budget lowered to 100 on the local stack, 150 concurrent requests answer 100 admitted and 50 `429`, where they
answered about 20 admitted and the rest `503` after 10 s before. When all of a burst arrives at the same moment, each
admitted request still costs one write on TerraBase's single writer, and an over-limit request's own increment waits
behind them, so the time to its `429` grows with the burst.

## Regions

Each budget keeps one count per region (gru, sjc, fra); a region writes only its own, and a read sums the three. A
replica whose clock is behind never resets a window another replica has already advanced. A fixed window's increment
runs at the database's home, where every region's count is current, so the maximum holds across regions even when a
region's read lags: an increment that would pass the maximum changes nothing and the region reads again, and a read
that keeps lagging ends in `503` after at most 64 rounds. A region's first charge of a budget creates its count at 1
without that check, so in the window where the other regions create theirs, each can admit one more than the maximum;
the count then stays for every later window. Cooldowns and token buckets still decide on the other regions'
counts as the request read them, so a burst in several regions at once, or a lagging read, can admit more than their
maximum. ts-auth runs only in gru today.
