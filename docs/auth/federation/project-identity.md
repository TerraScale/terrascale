# Project social identity binding

Project Google, Apple, GitHub and custom OIDC callbacks now resolve the tenant-local
account by provider, trusted issuer/authority, client ID and stable subject.
OIDC requires a nonblank subject of at most 256 characters in the validated ID token;
signature, issuer, audience, lifetime, nonce, PKCE where supported and verified-email
checks remain. Only after validation, Google's accepted `accounts.google.com` and
`https://accounts.google.com` spellings bind to canonical `https://accounts.google.com`.
Other OIDC issuers retain exact validated spelling without general URL normalization.
GitHub uses the fixed
`https://github.com` OAuth authority and positive numeric `/user` ID, with a verified
primary `/user/emails` address obtained using the same access token.

A bound identity resolves the same enabled local ULID after an upstream verified-email
change, even if another local account owns that address. Local email, its ownership
claim and recovery destination remain unchanged. Unknown subjects create a user,
email claim and provider claim atomically only when the email is unused and the
project permits signup. Signup disabled still allows already-bound identities.
Matching an existing email never attaches a new method or selects that account;
refusal gives generic existing-method guidance. Deliberate account linking is pending.
This explicitly supersedes the email-based account reuse in project-social-sign-in.md.

The sealed flow pins a hash of the provider configuration, including issuer, client,
Apple metadata and encrypted credential generation. Replacement or secret resealing
invalidates pending flows when observed before or immediately after code exchange.
These snapshot checks are not a transactional fence against later concurrent changes.
Deleting a browser cookie is not durable replay protection; provider code redemption
remains single-use. Provider configuration is absent by default and project-scoped.

Local MFA/session and disabled-account gates remain enforced. A bound account with an
active authenticator app gets the hosted second-factor page instead of a session, as
with password sign-in: a sealed, 5-minute, single-use challenge bound to the account
epoch/stamp. Upstream `amr`/`acr` never count as a factor. An unknown subject whose
email is taken is refused before any challenge.
Public token subjects remain local ULIDs; HTTP DTOs and SDK contracts are unchanged.
Previously email-only project accounts require an existing sign-in method; there is
no automatic backfill, migration or linking shim. Platform foundation storage policy
still applies; this slice changes no TerraBase storage format and performs no reset.
