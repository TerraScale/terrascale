# Dashboard MFA (TS-707)

The internal slices implement TOTP and recovery-code persistence, atomic
composition with the caller's authorization transition, and refusal of
primary-only authentication for users with active MFA. Hosted enrolment, the
second-factor sign-in step and organization policies are described in
[mfa-sign-in.md](mfa-sign-in.md); where the two differ, that file wins. WebAuthn
follows in a separate slice. Sign-in for the dashboard and
the ts CLI happens on ts-auth's hosted pages (HA1).

## Credential state

`mfa_credentials` holds one document per instance and user, addressed through
`AuthStore` and the pinned `ts-dotnet` SDK. Every operation checks the record's
owner and the user's current authentication epoch. There is no cache or
process-local coordination. Missing collections and storage failures fail
closed; they never select an in-memory store.

TOTP uses Otp.NET with a random 160-bit secret, SHA-1, six digits, a 30-second
step, and at most one step of clock skew in either direction. The secret is
encrypted with the existing master-key secret box and associated data bound
to its instance and user. Starting enrollment returns the provisioning secret
once and stores a pending enrollment that expires after ten minutes. A new
pending enrollment supersedes the previous one. A password reset or a sign-out
everywhere invalidates pending enrollment.

Confirmation verifies the pending TOTP code and atomically activates the
credential, records its consumed step, issues eight recovery codes, and
advances the user's authentication epoch. Disabling requires a current TOTP
or recovery proof and atomically clears the credential and recovery codes and
advances the epoch. These changes invalidate previous sessions, email codes,
and refresh tokens. Previously issued JWTs checked offline remain valid until
their expiry; offline consumers cannot observe the epoch change. Active MFA
survives password resets and sign-out everywhere; those operations never clear
the credential.

TOTP verification conditionally advances the last accepted time step, so a
step cannot work twice, even on different replicas. Recovery codes have more
than 128 bits of random entropy, are stored only as SHA-256 hashes, expire
after 365 days, and are removed conditionally when consumed. A successful
activation returns their plaintext once; they cannot be retrieved later.
Proof attempts share an account budget of ten per fifteen minutes in the
existing TerraBase attempt limiter. Invalid persisted credentials and
unreadable ciphertext fail closed.

Credential changes and proof consumption assert the user's authentication
epoch in the same transaction as the version-conditional credential write.
Enrollment and proof expiry are checked before submitting the transaction.
An acknowledged commit reports successful activation, verification, or
disabling even if the transaction completes after that validity window ends;
activation returns its recovery codes. Proofs already expired when checked
are rejected without changing credential state or the user epoch. An
ambiguous write withholds the result and is never compensated by restoring
a spent proof or clearing a credential. Replica clocks must be synchronized.
The storage authority's durability and failover limitations
documented in the port guide also apply to these security-critical writes.

Internal overloads let a trusted caller include a version-conditional change
to a separate authorization document in the same transaction as enrollment,
confirmation, verification, or disabling. A failed authorization precondition
leaves the credential, factor proof, and user epoch untouched. The caller must
validate its proof's tenant, user, client, purpose, secret, and expiry before
invoking the service; these overloads do not authenticate a user. Its authorization
deadline is checked again immediately before transaction submission, after
the asynchronous credential reads. Starting enrollment caps the pending
credential's expiry at that deadline and never renews reauthentication.
No factor result or recovery codes are returned before the transaction is
acknowledged. Enrollment start also reports its committed result when the
response arrives after expiry; the expired pending enrollment cannot
subsequently be confirmed.

## Deployment and next slice

Existing password and email-code grants (including sign-up for an existing
email), hosted session creation and resolution, authorization-code and
refresh redemption, UserInfo, platform management authentication, and token
exchange refuse users with active factors. They read and validate the
tenant/user-bound credential on every check, including its ciphertext;
corrupt or unavailable state grants nothing. Pending and disabled factors
permit primary authentication. UserInfo also checks the account epoch, so
password resets and sign-out everywhere invalidate old access tokens there.
Machine client credentials and API keys are independent of user MFA.
Unavailable or corrupt MFA state returns OAuth 503 errors at the token and
UserInfo endpoints with `Cache-Control: no-store` and `Pragma: no-cache`.

The current refusal is deliberately the existing generic credential error,
not an MFA challenge: no proof-bearing sign-in flow exists yet. A refused
email-code sign-in can consume its code but never creates a session or token.
Session creation checks the account epoch in the same transaction as the
session write; activation or revocation that wins first prevents issuance.
Other authentication checks re-read the epoch after checking factor state.
An authentication concurrent with activation may complete if its final check
preceded activation, but online checks subsequently reject its old epoch;
JWTs already accepted offline remain subject to their documented expiry limit.
Password reset remains available and never disables an active factor.

User sign-in access and identity tokens carry `auth_time`, the server-issued
time of authentication in Unix seconds. Refresh retains that time;
organization/project exchange copies the original numeric value from the
validated sign-in token. A submitted `auth_time` never changes it, and
machine/API-key tokens omit it. Exchange requires a nonnegative integer and
rejects missing or malformed times, including fractional or overflowing
values. It permits the issuer's authentication time to be at most 30 seconds
ahead of the exchanging replica, preserving that time without clamping it;
anything further ahead answers `invalid_grant`. Authentication time alone
does not prove factor completion or enforce an organization MFA policy.

Issuance in PR #63 precedes exchange continuity and mandatory timestamp
validation. Before deploying exchange validation, verify every regional
issuer has deployed PR #63 and every earlier access token has expired on
every replica. Wait at least one access-token lifetime (currently one hour)
plus the 30-second clock-skew allowance after the last old issuer stops
issuing. Record the verified rollout cutoff and earliest deployment time.
No authentication time is synthesized. Fast tests cover independent clocks
at and beyond the allowance, as well as original-time continuity after refresh.

Before deploying a release that requires `mfa_credentials`, explicitly
provision that collection in the existing auth database through the pinned
SDK. `AuthStore.ProvisionAsync` includes it, and `/readyz` requires it. Never
re-found a serving cell. No existing MFA data or legacy formats are carried
forward. TS-754 and TS-755 own the shared ID and uniqueness cutover; include
this collection in their clean dataset bootstrap. MFA reuses the account ID
and adds no uniqueness-claim documents.

The internal service assumes its caller has authenticated and performed the
reauthentication required for enrollment and disabling. It is not a public
authorization boundary. Before exposing it, later slices must add the
headless MFA API and OpenAPI contract with server-verified assurance that
survives refresh and token exchange. Tests of those HTTP boundaries belong
in `fast`. Organization
policies and WebAuthn must enforce their decisions on the server.
Persistent authentication challenges use the shared plain-ULID storage
boundary after the TS-754 cutover; this composition slice adds no challenge
entity, collection, or alternate addressing scheme.
