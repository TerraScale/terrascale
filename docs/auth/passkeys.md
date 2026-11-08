# Passkeys

ts-auth issues and verifies WebAuthn passkeys for platform (TerraScale)
accounts. A passkey replaces neither the password nor TOTP; it is an additional
credential a member enrolls from the hosted security page or through the
platform API, and a sign-in method on the hosted sign-in page whose user
verification satisfies the second-factor requirement. TOTP and recovery-code
MFA are described in [mfa.md](mfa.md) and [mfa-sign-in.md](mfa-sign-in.md).

Members list, rename and delete their enrolled passkeys through the platform
API and the hosted security page; deletion always requires fresh
reauthentication.

## Relying party and origins

The relying-party ID is the parent domain of the auth host: for
`auth.terrascale.tech` the RP ID is `terrascale.tech`, so a passkey created on
the hosted pages also works on `dash.terrascale.tech`. Accepted WebAuthn
origins are the auth service's public origin and the dashboard origin; outside
development the service refuses to start if either origin is not the RP ID
itself or a subdomain of it. Attestation conveyance is `none`; user
verification is required for every ceremony and resident keys (discoverable
credentials) are preferred.

## Endpoints

### Platform API (bearer token, member)

| Method and path | Purpose |
| --- | --- |
| `POST /v1/terrascale/account/passkeys/options` | Start enrollment: returns `PublicKeyCredentialCreationOptions` and a sealed challenge. Existing credential ids are excluded. |
| `POST /v1/terrascale/account/passkeys/verify` | Finish enrollment: sends the browser's registration answer, the sealed challenge and an optional name (1–100 characters). Stores the passkey and returns 201 with the credential id, AAGUID and creation time. |
| `GET /v1/terrascale/account/passkeys` | List the account's passkeys: id, name, AAGUID, creation time and last use. Public keys are never returned. |
| `PATCH /v1/terrascale/account/passkeys/{id}` | Rename one passkey (1–64 characters). |
| `DELETE /v1/terrascale/account/passkeys/{id}` | Delete one passkey; sessions and tokens stay. |

A refused ceremony, a reused or stale challenge, an authentication-state change
since the challenge was issued, or a credential id already enrolled answers
403 `passkey_denied`. Exhausting the attempt budget answers 429 with
`Retry-After`. Rename and delete are conditional on the account's
authentication state and the stored passkey list: a concurrent change answers
409 `version_conflict`, an unknown credential id 404 `passkey_not_found`, a
stale account state 403 `passkey_denied`.

Deletion requires a fresh approved `delete_passkey`/`ts-auth` step-up proof in
`X-TerraScale-Step-Up-Proof` (see [step-up-proof.md](step-up-proof.md));
exactly like account deletion, a bearer token alone never authorizes it, and
the 404 and last-factor checks run before the proof is consumed. Deleting the
last passkey of an account without an authenticator app that belongs to an
organization requiring two-factor authentication answers 409 `last_factor`:
add another passkey or an authenticator app first.

Enrolling a passkey is a sensitive credential change: the platform verify
endpoint additionally requires a fresh, approved, single-use `add_passkey`
proof in the `X-TerraScale-Step-Up-Proof` header
([step-up-proof.md](step-up-proof.md)); a bearer token alone answers 403
`step_up_required`. The options endpoint mints nothing persistent and needs no
proof.

### Hosted security page (session cookie, same-origin)

| Method and path | Purpose |
| --- | --- |
| `POST /ui/{slug}/security/passkeys/options` | Same creation options and sealed challenge as the API endpoint, for the browser's `navigator.credentials.create` call. |
| `POST /ui/{slug}/security/passkeys/verify` | Same verification and storage as the API endpoint. |
| `POST /ui/{slug}/security/passkeys/rename` | Same-origin form post with the passkey id and a new name (1–64 characters), exactly like the API rename. |
| `POST /ui/{slug}/security/passkeys/delete` | Same-origin form post with the passkey id and a code (the authenticator's current code or an unused recovery code) that reauthenticates the removal, as turning off two-factor authentication does. Records `member.passkey_removed` in the account's organizations' audit logs. |

All require a live session cookie and a same-origin request; anything else
answers 403 `passkey_denied`. Enrollment additionally needs a sign-in within
the last 10 minutes, as for adding a passkey through the API: an older session
answers 403 `passkey_denied` and the account signs in again first, and a first
enrollment right after sign-in (forced MFA) is always within the window. The
same 409 `last_factor` rule as the API guards the hosted removal, checked
before the authorizing code is spent. An account whose only factor is a
passkey has no hosted code to present; its member removes a passkey through
the API's step-up proof instead.

### Hosted sign-in (anonymous)

| Method and path | Purpose |
| --- | --- |
| `POST /ui/{slug}/sign-in/passkey/options` | Form post with the email address; returns `PublicKeyCredentialRequestOptions` listing that account's credential ids and a sealed challenge. |
| `POST /ui/{slug}/sign-in/passkey` | JSON post with the challenge, email, the browser's assertion and `next`; verifies it and opens the session. |

An unknown address gets the same response shape with an empty credential list,
and verification fails identically, so the flow does not reveal which addresses
have accounts. Every refusal is the same generic 403 `passkey_denied`. The
options endpoint also answers 503 when credential state cannot be read.

## Challenges

Both enrollment and sign-in challenges are sealed (secret box under the master
key, bound to the tenant), single-use and valid five minutes. A challenge dies
with the account's authentication state: any epoch or credential change after
it was issued makes verification fail. Nothing is stored server-side for a
challenge; consuming it is asserted in the same transaction that stores the
credential or updates the sign count.

## Sign-in and MFA semantics

A passkey sign-in requires user verification at the authenticator, so the
session it opens is already marked multi-factor: tokens carry
`ts_auth_mfa: "passkey"` and no second-factor step follows. A member whose only
factor is a passkey satisfies organizations that require a second factor and
may turn that policy on. A non-increasing signature counter is refused and the
credential's counter and last-use time are updated in the same transaction as
the challenge consumption. See [mfa-sign-in.md](mfa-sign-in.md) for how this
sits alongside the TOTP step.

## Limits

Enrollment verification charges 10 attempts per user per 15 minutes; sign-in
verification charges 10 attempts per normalized email address per 15 minutes,
and the hosted sign-in flow also charges the per-IP sign-in budget. Exhaustion
answers 429 with a `Retry-After` delay. A credential id is at most 1024 bytes
and a stored name at most 100 characters.
