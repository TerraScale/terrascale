# Single sign-on account proof

The organization OIDC callback requires an explicitly true email_verified claim
and a nonempty subject. An omitted or false verification claim is refused.

An identity link records the connection's domain and organization, the provider
issuer and subject, the user, and the connection's UpdatedAt version. All must
match exactly on a later callback, including the user selected by the verified
email. Any connection configuration change requires local proof again; there is
no newer-version exception. A different subject is a different identity.

An account created by this callback is saved with its membership and identity
link atomically. It needs no local primary credential. An existing account without
a matching link first sees a confirmation page naming the organization, the
identity provider's full issuer (including its path) and the email the provider
asserted. Only after the person chooses "Link this sign-in to your account"
(POST /ui/terrascale/sso/link, with a constant-time match of the displayed
continuation digest, recording confirmation inside the sealed continuation)
does one ordinary
sign-in complete the link and any new membership: a first credential (password,
emailed code or passkey), followed by the second factor when ordinary sign-in
requires one. An unconfirmed continuation is never used: posting a credential
without the confirmation deletes it and ordinary sign-in rules apply, so no link
is written. A stale confirmation form preserves the newer continuation and
displays its own confirmation page. Organization names omit control and
formatting characters for display; the name and email use bidirectional isolation.
A user-verified passkey completes ordinary sign-in by itself. An authenticator
or recovery code alone cannot link an existing account. Existing sessions alone
do not link identities. Social sign-in does not carry the continuation and
cannot complete the proof; an account that only has social sign-in uses an
emailed code. The asserted email is always in the organization's verified
domain, so whoever controls that domain's mail can complete the proof for an
account whose ordinary sign-in accepts an emailed code. That is the same access
mailbox control gives without single sign-on. The link's foreign keys delete it with the connection or the
organization membership.

The browser continuation lasts ten minutes from the callback, independently of
time at the provider. Invalid and expired cookies are explicitly deleted,
including when an error envelope replaces response headers. It binds the user
and authentication state, identity,
organization and connection version. A stale or unrelated continuation cookie
is deleted and ordinary sign-in continues. A matching continuation whose
connection changed requires a fresh SSO sign-in.

Linked accounts with an enrolled authenticator or passkey complete the factor
flow after the provider callback. Unlinked existing accounts first see the link
confirmation and then the ordinary password page; the callback never issues a
factor-only challenge.
A factor challenge issued for a linked identity is bound to that exact SSO
continuation and cannot complete proof for another identity.
Organizations requiring MFA send users without a factor to the existing
authenticator or passkey enrollment before completing sign-in. Successful
authenticator enrollment advances the continuation to the newly authenticated
account state. Required authenticator enrollment checks the continuation before
it activates the authenticator; if the continuation is refused after that (a
concurrent connection or membership change), the page still shows the new
recovery codes and no session is issued. Completion exceptions after activation
also return the recovery codes, including a refused session creation after
another authentication-state change. When the organization requires MFA,
the completed SSO session must carry MFA proof; a user-verified passkey counts
as that factor only inside an SSO continuation. Sign-ins without SSO keep the
platform's ordinary MFA rules. A non-SSO factor challenge omits the optional
ssoProof member and has the exact serialized bytes used without this change;
challenges in flight continue across deployment in both directions. An SSO
challenge carries that member and is refused by a previous build's strict reader.
Every authentication instance must run this build before SSO linking is relied on.

| Account and identity | Callback continuation | Link and session completion |
| --- | --- | --- |
| New account | Required MFA enrollment, if any | Account, membership and link are created atomically; required MFA completes before the session |
| Existing account, matching link | Enrolled factor or required enrollment, if any | Exact link and connection version are checked before the session |
| Existing account, no matching link, no factor | Link confirmation, then the ordinary password page | Confirm, complete ordinary first-credential sign-in and required enrollment, if any, then link |
| Existing account, no matching link, has factor | Link confirmation, the ordinary password page, then the ordinary second factor | Confirm and complete both steps before linking; a complete ordinary passkey sign-in also counts |
| Existing account, no matching link, credential posted without confirmation | The unconfirmed continuation is deleted | Ordinary sign-in rules; no link |
| Existing account, no matching link, factor-only POST | A challenge issued outside this continuation does not match its digest | No session and no link |

The completion transaction asserts the exact user row whose epoch and stamp were
validated against the continuation, alongside any newer row read by membership
planning. A reset between validation and the write therefore prevents both the
membership and the identity link from committing. Connection and organization
rows, existing identity links, new-account creation, factor activation and
session creation also retain their conditional writes.

## Ordinary credentials and session paths

The only writer for an existing account is FinishProofAsync, called by
SignInFlow.SignedInAsync after an ordinary session has been issued. It checks
the platform tenant, the account id, epoch and stamp, current connection and
membership, and any required MFA. It rejects impersonation-ticket bindings.
Both linked-identity factor challenges and ordinary factor challenges issued
during this browser continuation carry its exact digest. Completion compares
both nullable digests, so a challenge issued without a continuation cannot be
transplanted into one.

| Session or credential path | Can complete an existing platform account's proof? | Reason |
| --- | --- | --- |
| Email and password | Yes | Fresh ordinary session after the password and any enrolled or required factor |
| Emailed sign-in code | Yes | Mailbox control is the first credential; the same second-factor rule applies |
| User-verified passkey sign-in | Yes | A complete ordinary passkey sign-in proves that account |
| Ordinary TOTP, recovery code or required enrollment | Yes, after the first credential | The challenge is bound to this exact continuation; factor-only submission cannot create proof. Required enrollment checks the continuation before activating the factor |
| Hosted sign-up verification | Only when it redeems an existing account's sign-in code | A newly created local account cannot match an existing continuation; the shared sign-in completion still enforces SSO |
| Already linked SSO identity | Yes, only for the same existing link | The callback holds the exact link version; enrolled factors and required enrollment still apply |
| Magic link | No | Magic links exist only for project tenants; a project session never matches a platform continuation's account and SignedInAsync reads continuations only for the platform tenant |
| Username and password | No | Username resolution is project-only; the completed project session cannot write a platform link for the same reason |
| Social sign-in, including emailed social enrollment | No | Project social sessions are excluded by tenant. Platform callbacks at /signin do not receive the Strict proof cookie scoped to /ui/terrascale; ProofFor also excludes that path. Without proof, enforcement revokes a new non-owner session; an unbound factor challenge cannot use a continuation on return |
| Administrator impersonation | No | Only project tenants can be impersonated. Tickets create separate impersonation records and cookies, never an IssuedSession; hosted mutations are read-only. The link writer additionally rejects the tenant and impersonation-ticket binding |
| Machine client credentials and user API keys | No | These issue or verify API credentials without creating a hosted browser session or calling SignedInAsync |
| Existing hosted session, refresh or credential rebind | No | Session resolution, token refresh and RotateAsync never call the link writer |

Enforced-domain checks run in SignedInAsync after proof handling and before the
cookie is written. A platform non-owner without valid proof is redirected to
SSO and the newly issued session is revoked. Platform magic-link issuance is refused with 403; redemption returns 404; platform username resolution stays disabled.
Neither project method authenticates the same-email platform account.

## Admission and organization boundaries

SignUpRestrictions.AdmissionAsync explicitly returns unrestricted admission for
the platform tenant. Organization SSO is platform-only and preserves main's
explicit SSO provisioning: account, platform membership and link are saved in
one atomic batch. Project modes public, restricted and waitlist govern project
accounts, with block rules taking precedence and restricted/waitlist requiring
an allowlist match or pending invitation. A project has no organization SSO
callback: /ui/{project}/sso is 404, including in restricted mode. SSO creating a
platform identity cannot create a project user or consume a project invitation.

Profiles use main's NewAccount defaults. Project organizations have separate
project_organizations and project_organization_memberships tables. This link
still references platform sso_connections(domain) and
memberships(organization_id,user_id), with cascades on both relationships.
Project organization membership cannot satisfy either foreign key.
