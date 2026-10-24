# Two-factor sign-in and organization policy (TS-938)

Builds on the credential slices in [mfa.md](mfa.md). Users can now hold an
active authenticator (TOTP) factor.

## Hosted pages

- `GET /ui/{slug}/security` shows the signed-in account's factor state and
  remaining recovery codes. Without a session it sends the browser to sign in
  and back.
- `POST /ui/{slug}/security/totp` starts enrolment and shows a QR code (PNG
  data URI, QRCoder) and the base32 key. It needs a session opened in the last
  15 minutes; older sessions are asked to sign in again.
- `POST /ui/{slug}/security/totp/confirm` activates the factor, advances the
  epoch (every session and token ends), opens a fresh two-factor session for
  this browser and shows the eight recovery codes once.
- `POST /ui/{slug}/security/totp/disable` needs a current TOTP or unused
  recovery code, removes the factor, advances the epoch and opens a fresh
  session.

Every post is same-origin checked like the sign-in forms.

## Sign-in

After a correct email code, password or existing-email sign-up, an account
with an active factor gets the second-factor step instead of a session. The
step carries a sealed challenge (secret box under the master key, bound to the
tenant) naming the user, its epoch and state stamp, the proof lineage of the
first step, and a five-minute expiry. Nothing is stored for it: a sign-out
everywhere, reset or factor change voids it, and every attempt still needs an
unused TOTP step or recovery code and charges the account's ten-per-fifteen
minutes MFA budget plus the IP sign-in budget. The challenge is only issued
when the user read after the factor still matches, so a concurrent activation
or revocation yields the generic refusal.

`POST /ui/{slug}/sign-in/mfa` redeems the code and opens a session marked
`mfa`. Sessions without the mark are refused for accounts with an active
factor. Tokens issued from a marked session carry `ts_auth_mfa: "totp"`;
refresh, UserInfo, platform API authentication and token exchange accept an
active-factor account's token only with that claim. Activation and removal
advance the epoch, so a token's claim always matches the factor state it was
issued under.

The device flow (ts CLI) works for two-factor accounts: the approving browser
session already proved the factor.

## Organization policy

`PUT /v1/terrascale/organizations/{organization}/mfa-policy` with
`{"require_mfa": true|false}` needs the owner role; turning it on needs the
owner's own factor (409 `mfa_not_enrolled`). While it is on, a member without an
active factor gets 403 `mfa_required` from every organization- or
project-scoped endpoint (including API key creation) and `access_denied` from
the organization/project token exchange. Listing organizations still works and
shows `require_mfa`, so the dashboard can send the member to enrol. Existing
API keys keep working: they are the organization's credentials.

`GET /v1/terrascale/account/mfa` returns the caller's `enabled` state,
`recovery_codes_remaining` and the security page URL.

## Audit

Policy changes write `organization.mfa_required` / `organization.mfa_optional`
in the same transaction. Enabling, disabling and signing in with a recovery
code write `member.mfa_enabled`, `member.mfa_disabled` and
`member.mfa_recovery_code_used` to each of the user's organizations, best
effort after the change.

## Storage

No new collection and no format change: sessions gain an optional `mfa`
boolean and organizations an optional `requireMfa` boolean; missing values
read as false.
