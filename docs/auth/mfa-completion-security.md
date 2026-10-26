# Second-factor challenge completion and factor-change commits

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

## Factor changes commit only for a live caller session

Hosted enrolment start, confirmation and removal each bind the caller's
session in the same TerraBase transaction as the factor change:

- Enrolment start asserts that the session is still stored, not revoked,
  not expired and at its authentication state. The commit must land before
  the session expires and before the 15-minute reauthentication window ends.
  The pending secret never outlives that window.
- Confirmation and removal delete the caller's session at the version that
  authorized the request. A sign-out, rotation or revocation that lands first
  changes that version, and the factor change does not commit. After a
  successful commit, the endpoint opens the replacement session.
- A refused commit whose session is gone redirects to sign-in. A storage
  outage fails closed (503).

Process-local state plays no part in either rule. Both depend only on
TerraBase transaction preconditions.
