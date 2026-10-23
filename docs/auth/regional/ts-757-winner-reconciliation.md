# TS-757: regional credential winners

This contract supersedes the family-wide revocation proposal in
`docs/terrabase/replication/17-ts-auth-on-lattices.md` for ts-auth. It does not
promise globally exclusive success before replication.

Each ts-auth instance reads and conditionally writes its configured regional
TerraBase copy, through ts-dotnet. There is no gru home, remote fallback, or
remote authority client. A local outage fails that region's request.

## Winner and credential validation

A local one-use operation atomically creates an immutable `uniques` claim and
consumes its source record. Concurrent regions can both acknowledge that
operation. TerraBase joins their claims using its existing `(HLC, replica id)`
entry ordering; ts-auth never compares application clocks or contender IDs to
choose a winner. A claim has one scalar `ownerId`; ts-auth never updates or
reclaims an immutable credential claim. A later stale PUT to a source or child therefore
cannot promote the losing branch by rewriting the winner pointer.

A `CredentialBinding` identifies the claim kind, issuance key, and contender
owner. Session rotation and OAuth redemption copy every ancestor binding into
their successors. Every session resolution and stored OAuth token lookup reads
all those claims from the regional copy. A missing claim or different owner
rejects the credential. The `credential:user-state` binding is different: it
reads the scoped user and email owner and requires the bound `authStamp`.
A descendant rotated again during a partition still
retains its losing ancestor. After all relevant deltas arrive, both regions
make the same decision; no region continues authorizing both branches. Losing
records can remain stored for expiry/cleanup; their presence grants nothing.

Email lookup and lookup by user ID both verify the current scoped email claim.
A losing duplicate user therefore cannot authenticate by its ID, and its
sessions and user grants fail too. One pending sign-up issuance fixes its user
ID before redemption, so redeeming that same issuance in two regions cannot
create two different account owners. Account creation assigns a fresh state
stamp per contender. Signup descendants bind the email owner and user state,
so the same account ID cannot keep two successful creation branches alive.

Passcode issuance has an `issueId`, independent of the six-digit value. Each
ordinary issuance atomically claims its predecessor slot and copies all
issuance ancestors. Concurrent different codes from that slot therefore share
a permanent winner claim. Later resends add a new claim; they never rewrite an
earlier claim or invalidate a previously completed ordinary sign-in.
An ordinary sign-in redemption adds its own winner binding. The internal
`RedeemAsync` user result carries these proofs plus the user-state binding as
transient `AuthenticationBindings`; callers opening a
session or issuing OAuth credentials must preserve it. `StoredChallenge`
similarly returns `Binding`; an internal caller creating a descendant must
persist it and call `AreCredentialsCurrentAsync` on use. There are currently no
production callers of the low-level stored-challenge API.

OAuth references retain private lineage in their encrypted principal and
persist it on token entries, including sign-in access-token entries. Issuance
captures lineage before OpenIddict filters private claims from JWT payloads.
Losing/replayed OAuth credentials return `invalid_grant` at the token endpoint,
with a notice naming expiration, prior use, or a concurrent regional sign-in or
rotation and directing the user to sign in again. Userinfo and ts-auth's
management authentication reject the losing token entry. Replaying a parent
never revokes the winning authorization. Explicit RFC 7009 revocation still
revokes the authorization. OpenIddict entity caching is disabled so payload
and authorization lookups observe the current regional storage winners.
Device approval atomically creates a separate
subject-bound authorization while consuming the user code. Its ID travels
only in the encrypted device payload as `ts_auth_device_authorization`;
the shared subjectless parent authorization is never rewritten. At device
redemption, the winning payload selects that subject-matching authorization
for the issued credentials. An inherited `credential:authorization` binding
requires the original parent to remain valid, including after refresh or API
key delegation. This proof is deduplicated and counted toward the 64-proof
budget before approval mutates storage. Redemption into separate child
credentials additionally adds the ordinary one-use winner binding.

A user authentication-state change also writes a random `authStamp`. Sessions,
passcodes and user JWT claims bind that value as well as the numeric epoch.
Two concurrent password resets can both advance epoch N to N+1. Password reset
uses `ResetPasswordAsync`: one local transaction consumes the source passcode,
creates its one-use claim, and conditionally replaces the user at the original
epoch **and** stamp. There is no consume-then-password-write gap. The single
user LWW record (password, epoch, stamp) is the authoritative reset winner;
the separate claim's document clock does not choose a different password.
Reset descendants bind that state stamp, rather than the independent consume
claim. A delayed same-epoch reset with a different source stamp fails.

Delegated API-key secrets retain their caller's lineage independently for the
current and grace secrets; missing lineage fails closed. This includes the
user-state binding: a later password reset, recovery, MFA state change or
sign-out-everywhere invalidates keys delegated from the old state too.
Password hashes and API-key digest/grace fields use TerraBase's existing full-document LWW PUT.
API-key token exchange rereads the current regional digests. After convergence
only the winning new secret (and the explicitly retained previous secret
within its existing grace) exchanges successfully. Invalid key errors mention
revocation, expiration and concurrent rotation without identifying another
user or tenant.

## Storage and rollout handoff

New persisted fields are `users.authStamp`, `sessions.authStamp` and `bindings`,
`passcodes.authStamp`, `issueId`, `issuanceBindings`, `resetsAuthentication`
and `pruned`, `oidc_tokens.bindings`, API-key per-secret bindings, and
`verification_tokens.binding`. Winner claims live in the existing `uniques`
collection under immutable `credential:*` kinds, except `credential:user-state`
which reads the user directly and `credential:authorization` which reads the
original device authorization. Approval authorization records use the existing
authorization schema. No SDK expansion or new collection is used.

**Needs format bump.** The auth persisted schema changed; `StorageFormat.cs`
is unchanged. HANDOFF: the orchestrator/deploy lane batches the format bump
and reviewed reset/rollout. This feature branch does not perform it.

API-key records additionally persist `secretBindings` (`SecretBindings`) and
`previousSecretBindings` (`PreviousSecretBindings`) beside their respective
digest/grace fields. Management authentication returns the stored sign-in
token's bindings on the transient `UserRecord.AuthenticationBindings`;
API-key creation and rotation receive that authenticated user, validate its
tenant and current bindings, and persist them with the newly issued secret.
Rotation preserves the previous secret's own bindings during its grace.
The inherited `credential:user-state` binding checks the user's winning
`authStamp`, so later password resets or sign-out-all also invalidate derived
key secrets, including previous secrets still within grace.
Both client lookup and final token issuance validate each digest's bindings
independently. A losing sign-in's key cannot keep obtaining new JWTs after
its claim converges, even after the original sign-in JWT expires.

Historical API-key records have no reconstructable sign-in lineage. Missing
binding fields fail closed during digest resolution; they are not defaulted
to a safe empty chain. A newly issued secret always stores its actual binding
list, including an explicit empty list when its authentication has no one-use
ancestors. Existing keys need an operator-reviewed replacement or rotation
from a fresh valid sign-in; this change does not infer old ancestry or perform
that rollout automatically.

This is an auth-record change, not an automatic production migration or reset.
The owning rollout lane must review existing records and outstanding credentials before
rollout. Tests recreate isolated data with this schema. Do not delete serving
production state to make old records match. No production reset, deployment,
credential change or multi-region activation is authorized by this document.

Proof lists are bounded at 64, including state and issuance proofs. Rotation
never drops ancestors: a session at the bound returns HTTP 401
`reauthentication_required`; OAuth returns `invalid_grant`, both directing the
user to sign in again. A normal sign-in code retains at most 62 issuance
proofs, reserving room for state and redemption. The next request issues a
fresh email-possession recovery code. Redeeming it atomically advances the
user epoch/stamp and starts a fresh family; it also signs out older sessions.
This works for passwordless accounts. Competing recoveries use the same
authoritative user-state winner as password resets. Hosted code pages state
that recovery signs out older sessions. No operator reset is needed.

Passcode pruning clears expired code/password/pending-token material and marks
the record `pruned`, but retains its issuance anchor. Resends reuse one record
per subject. Immutable claims are intentionally retained; there is no claim
garbage collector or speculative ancestry compaction. Validation performs at
most 64 proof checks (the state proof reads user and email owner); resource
cost per credential is bounded, while historical claim storage is not pruned.

## Regional attempt counters

The limiter reads the fixed serving set gru/sjc/fra. Each subject has at most
three tenant-scoped HMAC(`purpose:region:subject`) bucket documents, reused on
rollover. It sums regional `Used` values in the effective window (maximum of
current and persisted windows), preserving the slower-clock future-window
rule. Each request conditionally changes only its region's component. This
assumes one independent TerraBase writer per region; multiple auth replicas
sharing that writer are supported. Invalid persisted data fails closed;
cooldown and notice deadlines remain persisted. Replication delay can admit
duplicate attempts temporarily. Lockout notices are best effort; globally
exclusive notice delivery is not promised.

## Offline JWT security model: accepted five-minute window

The accepted policy keeps TerraBase JWT authorization offline. Store
reconciliation stops losing sessions, refresh tokens and key-secret exchanges
at ts-auth after the required winner deltas arrive. It cannot revoke a JWT
already issued to an offline consumer. A losing access token may therefore
continue authorizing until its signed `exp`.

All newly issued ts-auth access JWTs live at most five minutes: ordinary
sign-in/service tokens use `OidcIssuer.TokenLifetime`; organization/project,
API-key and GitHub Actions exchanges use `PlatformGrants.Lifetime`. Subject
exchange also caps this at the subject token's remaining lifetime. Identity
JWTs use the same five-minute issuer lifetime. Browser sessions and reference
refresh tokens keep their 14-day lifetime but consult stored winner ancestry
on use; a losing refresh is rejected after convergence instead of obtaining
another five-minute access token.

TerraBase's offline consumer is
`src/TerraBase/Features/UserAuth/UserAuthenticationHandler.cs`, with successful
signature-validation caching in `ValidatedTokens.cs`. Cold and cached requests
must reject at `exp`; clock tolerance must not extend expiration. No per-request
call to ts-auth or winner introspection is introduced. Other offline consumers
must also enforce `exp` without extending this window; no sibling product is
edited here.

There is no fixed replication deadline during a partition. Before the local
winner deltas arrive, a branch can still issue tokens. Once they arrive,
ts-auth rejects the losing refresh/exchange; its last issued offline access
JWT has at most five minutes remaining. This accepted expiry window replaces
the earlier requirement for immediate offline JWT revocation at convergence.

Already-issued JWTs cannot be shortened retroactively: the prior ordinary
lifetime was one hour and platform exchanges were up to 15 minutes. Rollout
must allow those outstanding tokens to expire before claiming the new
five-minute bound across all traffic. Resetting stored auth records alone
does not revoke an existing offline JWT.

## Evidence boundary

The production auth project has been built locally during implementation.
The test owner supplies the real two-region TerraBase/session/OAuth/API-key
convergence results separately. No multi-region production result is claimed:
production currently serves gru, and sjc/fra regional auth DNS and live proof
remain absent. Readiness/global DNS alone do not establish regional serving
or conflict convergence.
