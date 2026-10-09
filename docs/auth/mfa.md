# Dashboard MFA (TS-707)

The first slice implements internal TOTP and recovery-code persistence. It
does not expose enrollment endpoints or enforce MFA during sign-in yet. The
headless API, WebAuthn, and organization policies follow in separate slices;
dashboard authentication stays on the dashboard's native pages.

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

## Deployment and next slice

Before deploying a release that requires `mfa_credentials`, explicitly
provision that collection in the existing auth database through the pinned
SDK. `AuthStore.ProvisionAsync` includes it, and `/readyz` requires it. Never
re-found a serving cell. No existing MFA data or legacy formats are carried
forward. TS-754 and TS-755 own the shared ID and uniqueness cutover; include
this collection in their clean dataset bootstrap. MFA reuses the account ID
and adds no uniqueness-claim documents.

The internal service assumes its caller has authenticated and performed the
reauthentication required for enrollment and disabling. It is not a public
authorization boundary. Before exposing it, the next slice must add the
headless API and OpenAPI contract together with login, hosted-session,
authorization-code, refresh, and token-exchange checks that prevent bypassing
an active factor. Tests of those HTTP boundaries belong in `fast`. Organization
policies and WebAuthn must enforce their decisions on the server.
