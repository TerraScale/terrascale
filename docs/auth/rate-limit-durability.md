# Rate-limit bucket durability

ts-auth sends every TerraBase write with `TB-Durability: remote_durable`, so credentials, sessions and revocations survive
the loss of one region. Rate-limit buckets (`rate_limits`, written by `AttemptLimiter`) are the one exception: they are
written `local_durable`.

## Why

Every token request charges an attempt: it reads the regional bucket documents and conditionally writes one. With
`remote_durable`, that write waited for another region's acknowledgement, and a lost compare-and-set (412) repeated the
round trip. On 2026-10-26 a client-credentials token took 2.25 s on average in gru (about 2.4 durable writes per token
at about 0.7 s each). Together with the document calls, that pushed the status Document API probe past its 5 s timeout.

## What changes

- Writes whose path targets `/collections/rate_limits/` carry no `TB-Durability` header, so TerraBase answers once this
  region's commit is synced to disk.
- Every other write keeps `remote_durable`. Reads are unchanged.

## Trade-off

If a region is lost before a bucket write replicates, that bucket reverts to its last replicated count, so its window
admits up to that many extra attempts once. Buckets are fixed one-minute windows (or bounded cooldowns), so the effect is
bounded by one window per lost region. Credentials, sessions, revocations and audit events are not affected.
