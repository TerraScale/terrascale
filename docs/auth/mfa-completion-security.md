# Second-factor challenge completion

This file updates [mfa-sign-in.md](mfa-sign-in.md). Where they disagree, this
file is correct: a completion claim is now stored for each challenge.

## A sign-in challenge completes once

The sealed second-factor challenge from the first sign-in step has its own
identifier. The transaction that consumes the TOTP step or recovery code also
claims that identifier. As a result:

- A challenge opens at most one session. After the first completion, another
  unused TOTP step or recovery code presented with the same challenge gets
  the expired page (401) and has to start sign-in again. This holds on any
  auth replica using the same regional store, because the claim is durable.
- A completion refused by the regional claim commits nothing, so its proof
  stays unused. Regional contenders may both commit before reconciliation.
- A wrong code, an expired or forged challenge, or a throttled request never
  completes the challenge. A throttled request keeps a live challenge
  (`Retry-After` comes from the persisted budget).
- The session opened from a completion carries the claim in its credential
  lineage. If two regions complete the same challenge before replication,
  only the replicated winner's session stays live, as with email codes.
- If the completion commits but the session then cannot be opened, the
  browser starts sign-in again.

