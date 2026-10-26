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

- Enrolment start asserts, in the transaction, that the session is still
  stored, not revoked and at its authentication state, and that it had not
  expired when the request sampled the time. Immediately before submission,
  the change is refused if the session has expired or the 15-minute
  reauthentication window has ended. Those deadlines are checked before
  submission, not at commit: a commit the store acknowledges is kept even if
  it lands after a deadline. The pending secret's expiry is capped at the
  earlier of the two deadlines.
- Confirmation and removal delete the caller's session at the version that
  authorized the request. A sign-out, rotation or revocation that lands first
  changes that version, and the factor change does not commit. The session's
  expiry is checked immediately before submission. After a successful commit,
  the endpoint opens the replacement session from the user state that commit
  wrote, never from a later read. A sign-out everywhere that lands in between
  refuses the replacement.
- A refused commit whose session is gone redirects to sign-in. A storage
  outage fails closed (503).

Process-local state plays no part in either rule. Both depend only on
TerraBase transaction preconditions.
