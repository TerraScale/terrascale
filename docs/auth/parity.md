# C# port capability tracker

The [zero-configuration contract](zero-configuration.md) is the required
production policy. Option names below inventory the current implementation;
manual switches, pre-created secret dependencies and optional delivery paths
remain gaps. They are not production setup instructions. Tenant authorization,
customer enrollment and storage safety/consistency requirements remain binding.

ts-auth now runs as the C# service (epic TS-424). The Rust workspace was
removed in TS-442 (`8dd2874`). Its source and tests before that commit are the
behavioural reference for each port stream, including the TS-428 store design.
A capability counts as served only when its C# endpoints and integration tests
have landed.
Nothing here implies acceptance against a live TerraBase deployment.

| Capability | Port stream | C# status |
| --- | --- | --- |
| Configuration, production guards, error envelope | TS-426 | Served; fast tests cover required and malformed settings, secret-free diagnostics and the envelope |
| Health, readiness, OpenAPI, Scalar, bundled assets | TS-426 | Served; fast tests cover version, fail-closed readiness, contract completeness, Scalar rendering in Chromium without third-party requests, asset headers. Slow tests cover probe coalescing, timeouts, stuck checks and waiter cancellation |
| Telemetry (Prometheus `/metrics` on port 9092, OTLP traces and logs) and GlitchTip error reporting | TS-426 | Served; a fast test covers `/metrics` only on the metrics listener; fast tests assert that no secret reaches GlitchTip or any log provider |
| Security primitives (Argon2id, AES-GCM, JWT/JWK, TOTP, WebAuthn) | TS-427 | Partly ported (TS-488): Argon2id via libsodium (PHC, m=19456, t=2, p=1; verification refuses hashes above 64 MiB or above the memory × passes cost of the RFC 9106 64 MiB, t=3 profile), the AES-256-GCM secret box under the master key with record-bound associated data, random tokens, SHA-256, HMAC and fixed-time comparison, ES256 keys, public JWKs and JWT signing and validation (Microsoft.IdentityModel, ES256 only; kid, issuer, exact audience, `typ` and expiry enforced). Token lifetimes are capped at 24 hours; Rust JWT templates allowed any lifetime, so the template port (TS-439) adopts the cap. Fast tests cover reference vectors, malformed hashes, tampering, unknown or missing kids, ES384 and HS256 headers, and wrong `typ`, audience or lifetime. Not ported: importing untrusted JWKs, RS256 and kid-less verification for upstream OIDC (TS-495 federation), HS256 keys, PKCE (TS-491), TOTP and WebAuthn (TS-495) |
| TerraBase Documents v1 persistence, uniqueness and single-use challenges | TS-489 (TS-428 design) | Implemented through ts-dotnet; create-only and ETag/If-Match CAS, deterministic scoped UUIDs, owner-checked release and retained consumed challenges. Local Release checks pass (100 fast, 21 slow, zero skips); live production activation gated on durability/recovery and TS-470 |
| Key ring, email, operator instances, rate limiting | TS-431 | Partly ported (TS-488): per-instance ES256 key ring with startup bootstrap and scheduled rotation (TS-703: a new key every 30 days, published two days before it signs; keys retire two days after their successor activates; at most 16 keys), the shared attempt limiter (one-minute windows), Cloudflare Email Sending over HTTPS (replacing SMTP) with the logging fallback; observability landed in TS-426. Lookup and rotation validate the ring's count and timestamps. Malformed, oversized and unreadable rings fail closed unchanged. Each signing key lives in its own TerraBase `signing_keys` document, sealed under `TS_AUTH_MASTER_KEY` from OCI Vault; retirement uses version-conditional deletes. The limiter uses conditional writes on the TS-489 store (`rate_limits`); any storage failure other than caller cancellation answers 503. Fast and slow tests run against the real pinned TerraBase, including two replicas racing. Limiter bucket ids are HMAC-keyed from the master key; callers pass identifiers the service already holds, and a caller limiting by attacker-chosen text (Rust's guest-mailbox folding) ports with that caller. Without Cloudflare email settings outside Development, email is logged and a startup warning is emitted; whether flows that need delivery must refuse to start is decided with the first flow (TS-490). Operator instances, the management-key gate (with management keys, TS-490) and Data Protection keys in TerraBase are not yet ported |
| Users, sessions and email passcodes | TS-490 (TS-432 design) | Implemented on the TerraBase store; served only through the TS-491 hosted sign-in page (see below) |
| Hosted email-passcode sign-in for existing accounts | TS-491 | Served at `/ui/terrascale/sign-in` for the `terrascale` instance; fast test covers a passcode sign-in that resumes `authorize` |
| Headless API for ts-dashboard: sign-up with email verification, password sign-in, email-code sign-in, password reset, RFC 7009 revocation | TS-582 | Removed (user order 2026-10-22); ts-dashboard signs in through the hosted pages as a plain OIDC client. RFC 7009 revocation stays served |
| Hosted sign-in (email code, password), sign-up and password-reset pages, signed-out page, v3 design | HA1 | Served for the `terrascale` instance |
| Email links, account and session-list pages, management users/sessions/keys | TS-432 design | Not implemented |
| Auth email templates, branding and copy API | TS-583 | Served: HTML + plain-text emails in `en` and `pt-BR` for every kind (the sign-in code, email verification and password reset are sent today), and `/v1/{slug}/email/*` for the `ts-dashboard` client: branding, per-kind copy with `{{ name }}` placeholders only, previews, rate-limited test sends, versions and restore. Fast tests cover authentication, validation, encoding, conflicts under concurrent saves, rollback, snapshots of every kind and the next code email using the saved branding. Sender domains (custom From addresses with sender-domain DNS authentication) are not implemented |
| OAuth 2.0/OIDC provider on OpenIddict: authorization code with S256 PKCE, rotating refresh tokens, discovery, JWKS, userinfo, RP-initiated logout | TS-491 (TS-433 design) | Served for one issuer, `{public origin}/terrascale`, and the `ts-dashboard` client. ES256 access tokens (`at+jwt`, configured audiences, email claims) and identity tokens from the key ring; client credentials for configured service clients; exactly-once code and refresh redemption with reuse revocation. Fast test covers sign-in, code redemption, token verification with IdentityModel, userinfo and identity-token rejection (see below) |
| Introspection, client management API, token pruning | TS-433 design (not in TS-491) | Not implemented; TS-704 adds peer-IP admission limits on every token grant and IP/email limits on hosted sign-in |
| Device flow (RFC 8628) for the ts CLI, approved on the hosted device page | HA1 | Served for the `terrascale` instance and the `ts-cli` client |
| Guest accounts, browser CORS | TS-495 (TS-433 design) | Not implemented |
| MFA (TOTP, recovery codes, step-up) | TS-707 (TS-434 design) | Internal TOTP and recovery-code persistence implemented; no MFA HTTP endpoints or sign-in enforcement yet. [Slice status and deployment prerequisite](mfa.md). Email passcodes moved to TS-490 |
| Passkeys | TS-435 | Not yet ported |
| OIDC, GitHub and SAML federation | TS-436 | Not yet ported |
| Platform organizations, projects, organization and project roles | TS-598 | Served under `/v1/terrascale/…` with the caller's sign-in access token (see below); contract in [`platform-contract.md`](platform-contract.md) |
| Invitations, ownership transfer, audit of the platform | TS-598 | Served: email invitations to an organization or project role, ownership transfer, and audit events written with every change (see the [port guide](csharp-port.md#invitations-ownership-transfer-and-audit)) |
| API keys exchanged for organization and project tokens (client credentials, token exchange) | TS-598 | Served: organization and project keys, rotation with a grace, the client-credentials and token-exchange grants (see the [port guide](csharp-port.md#api-keys-and-organization-and-project-tokens)) |
| Per-project tenants (one issuer per customer project) | TS-598 | Partly served: `{origin}/{project id}` issuers with their own key ring, discovery and JWKS while the project exists (404 otherwise); their OAuth endpoints answer 404 until the project tenant surface (contract slice 9). Fast tests cover issuer, keys, isolation from the platform's keys, and 404 for unknown and deleted projects |
| `usage:read` and `billing:*` scopes, project suspension, platform administrators, ts-auth's own storage token | TS-598 | Contract v1.1 published; not served |
| End-user organizations of a project tenant, enrollment domains, authorization checks | TS-437 | Not yet ported |
| SCIM directories, users, groups (including operator repair runbooks) | TS-438 | Not yet ported |
| JWT templates, webhooks, signed audit log | TS-439 | Not yet ported |
| Hosted admin portal (Datastar) | TS-441 | Not yet ported; bundled assets are served |

## TS-489 storage and readiness

`AuthStore` uses per-feature collections with create-only writes and
version-conditional replacement/deletion. Generic mutations reject the three
infrastructure collections to protect their dedicated operations. Natural
keys are deterministic UUIDv8 addresses derived from SHA-256 over
length-framed instance, kind and key; new TerraBase document IDs must be UUIDs.
Email keys use trim and invariant lowercase. Uniqueness claims have one
create-only owner; release
checks that owner and the current version. Ambiguous writes remain unresolved
and must not trigger compensating claim release.

Challenges store only lowercase SHA-256 bearer-secret hashes, bound to
instance, purpose, subject and expiry. Consumption checks every binding and
expiry, then uses CAS to mark the record consumed and returns `Consumed=true`.
Retaining that record rejects replay and reissue at the same address. No
identity routes, users, sessions or OIDC token flows are added by this storage work.

`/readyz` checks that `uniques`, `verification_tokens` and `storage_health`
(and, since TS-490, `users`, `sessions` and `passcodes`) exist in the configured database, then creates, reads and conditionally
deletes a synthetic `storage_health` document. Missing state, insufficient
credential scopes and storage failures return 503; there is no provisioning,
repair or in-memory fallback. The existing one-second completed-observation
cache and two-second deadline remain; an unknown region is omitted.
`AuthStore.ProvisionAsync` is explicit operator/test setup of only those three
collections in an existing database. Feature collections belong to their
owning streams.

The real-backend integration coverage targets independent-client uniqueness,
instance/kind isolation, stale-version rejection, state across recreated
clients, challenge bindings/expiry/replay and readiness with missing
collections or invalid/read-only keys. Tests require the actual SDK-pinned
TerraBase binary locally and in CI; unavailable prerequisites fail instead
of skipping. Local Release validation passed: formatting, build, NuGet audit,
OpenAPI lint, 100 fast tests and 21 slow tests, with zero skips. Production/live
validation has not been performed. See [the port guide](csharp-port.md) for the exact SDK and
server pins and runtime configuration.

Runtime selects `<ts-auth-project-uuid>/auth`, with the production project
labelled `ts-auth` in organization `terrascale` (logical dataset
`ts-auth/auth`). An operator resolves that UUID through Management before
deployment; runtime uses only a `tb_…` Documents credential scoped for
`documents:read` and `documents:write`. The required HTTPS endpoint is an
explicit static override via `ITerraBaseEndpointResolver`; Atlas SDK discovery
belongs to TS-507. ts-auth creates its collections at startup in the seeded `platform-auth`
database; production activation remains gated by TS-470.

The pinned server's decisions 0003 and 0005 limit deployment: SQLite WAL
`synchronous=NORMAL` can lose acknowledged writes since the last WAL sync
after an OS crash or power loss, though a process crash alone preserves them;
replication `Sync` is unsupported. Every replica stores through
the designated region's (gru) TerraBase
node's internal listener (port 8081 on its NetBird address) (discovery and the JWKS at its own region's), with no automatic retries. Lost claim/consumption writes can
invalidate security guarantees. Live production activation remains gated on
an approved durability/recovery design and TS-470 verification; readiness
does not prove production durability. The [port guide's activation gate](csharp-port.md#production-activation-gate)
links the pinned decisions.

Persistent uploaded files are not implemented; admin assets and fonts are
embedded in the executable. If persistent files are added, they use RustFS
through S3.

## TS-490 users, sessions and email passcodes

Users, server-side sessions and email passcodes are internal services in
`src/TerraScale.Authentication/Features/Identity/`, registered in DI on the TerraBase
store. The TS-491 hosted sign-in page is the only route that serves them, for
the `terrascale` instance; there is no JSON route or management API. The fast
suite drives sign-in, rotation and sign-out over real HTTP through test-only
routes.

Users are `{id, instanceId, email, authEpoch, createdAt}` in `users`, with a
random UUID id. Emails are trimmed and lowercased invariantly, then checked
for plausibility: at most 254 characters, no whitespace or control characters,
exactly one `@` after a non-empty local part, and a domain of at least two
non-empty dot-separated labels (stricter than Rust, which accepted `a@.`).
Creation writes the email claim and the user in one TerraBase transaction, both
create-only, so a claim never exists without its account. A taken email returns
null. Lookup goes from claim to user and rechecks instance and email.

Sessions (`sessions`) use a 48-character alphanumeric CSPRNG token. Only its
SHA-256 is stored, and it is the document address. The lifetime is an absolute
14 days from authentication. Resolution checks the instance, the revoked flag, a
fixed-time hash comparison and expiry, then that the user exists in the
instance with an `authEpoch` equal to the session's. Every resolution reads
TerraBase, with no cache, so revocation is immediate on every replica. Rotation
conditionally revokes the old session first, then issues a successor with the
same expiry, so it never extends the lifetime; concurrent rotations yield one
successor. Sign-out sets `revoked` with a conditional write and is idempotent.
Sign-out everywhere advances the user's `authEpoch` with a conditional write,
which also voids outstanding passcodes. Every ten minutes each replica's
`SessionPruner` deletes revoked and expired session documents of every instance
in batches of 500, continuing while a batch comes back full and deletes something.
Each delete is version-conditional, so competing replicas share the backlog
safely; a dead session grants nothing while it waits.

The cookie is the existing `tsauth_session` (`BrowserSecurity.SetSessionCookie`,
declared in OpenAPI as `SessionCookie`): `Secure`, `HttpOnly`, `SameSite=Lax`,
`Path=/` and a browser-session lifetime. The server enforces expiry. The
test-only sign-in route revokes the presented session before setting the new
cookie.

Passcodes (`passcodes`, one document per user) are six CSPRNG digits that last
30 minutes, work once and die after 5 wrong guesses. At most 5 codes are issued
per user per hour, using a fixed window stored on the same document and updated
with conditional writes. A new code replaces the previous one. Only an
HMAC-SHA256 is stored, keyed by HKDF-SHA256 of the master key (info
`ts-auth passcode digests`) and bound to instance and user, so a database copy
alone cannot be brute-forced offline. Changing `TS_AUTH_MASTER_KEY` makes
outstanding codes fail. Comparison is fixed-time. A wrong guess is charged, and a
correct one consumed, in a conditional write that must win, so concurrent
guesses cannot exceed the limit and concurrent redemptions have exactly one
winner. Malformed codes are rejected without charging an attempt. A code is
bound to the user's email and `authEpoch` at issue. It is consumed before the
session is written: a failed session write costs the user a new code and never
yields two sessions. Unknown emails write nothing.

Differences from Rust: Rust stored SHA-256 of the code and had a 30-second resend
throttle. C# stores the keyed HMAC and uses the hourly issue cap instead.

Per-code limits live on the passcode document. TS-704 adds shared IP and
normalized-email budgets to hosted sign-in through the TerraBase
attempt limiter, including unknown emails. Code replacement never resets the
email budget. Exceeding a budget returns 429 with Retry-After.

`AuthStore.ProvisionAsync` now also creates `users`, `sessions` and
`passcodes`, and `/readyz` requires them: a database provisioned before TS-490
reports 503 until an operator runs `ProvisionAsync` again.

Not in TS-490 (remaining TS-432 surface; TS-491 adds the hosted sign-in page):
hosted sign-up and reset pages, magic links, account and session-list pages
(HA1 serves hosted sign-up, password sign-in and reset), the management API for users, sessions, impersonation,
instances and API keys.

## TS-491 OpenID Connect

TS-491 serves an OpenIddict 7.7.1 provider for the platform issuer
`{origin}/terrascale` (the origin is `https://auth.terrascale.tech` unless a
preview or test sets `TS_AUTH_PUBLIC_ORIGIN`), so ts-dashboard can sign in.
Each project is a tenant whose `{origin}/{project id}` discovery document and
JWKS are served; other slugs answer 404. The issuer and every advertised URL
derive only from the origin, never the Host header.

Implemented: the authorization code flow with S256 PKCE required for every
client, scopes `openid`, `email`, `profile` and `offline_access`, 1-minute
codes, 1-hour ES256 access and identity tokens signed by the instance's key
ring, opaque refresh tokens that rotate on every use with a fixed 14-day
lifetime, discovery, a JWKS read from the `signing_keys` collection (reused for at most a minute), userinfo,
RP-initiated logout and the hosted email-passcode sign-in. A code or refresh
token is redeemed with a version-conditional write, so concurrent redemptions
have exactly one winner; a losing or later redemption revokes the sign-in's
authorization, which rejects every token issued under it. The confidential client `ts-dashboard` is registered
from `TS_AUTH_DASHBOARD_*` at startup.

Stores live in `oidc_applications`, `oidc_authorizations` and `oidc_tokens`,
with create-only or version-conditional writes. `AuthStore.ProvisionAsync` now
also creates them and `signing_keys`; `/readyz` requires all
of them.

Client credentials are served for configured service clients only.

Not implemented: introspection, guest
accounts, CORS, per-instance issuers, token and authorization
pruning. TS-704 adds peer-IP admission limits on every token request. After sign-out everywhere or logout,
access tokens already issued stay valid until they expire (at most 1 hour).
The [port guide](csharp-port.md#openid-connect-ts-491) has the details.

## TS-582 headless API

Removed by user order 2026-10-22: the password and email-code grants and the
`/{slug}/headless/v1/*` routes are no longer served, and TS-631's pending
sign-up resend went with them. ts-dashboard is a plain OIDC client of the
hosted pages. The sign-up, password, reset and limit logic the hosted pages
use stays; the [port guide](csharp-port.md#accounts-passwords-and-limits) has it.

## TS-598 platform organizations and projects

The management API's first slice serves organizations, organization members,
projects and explicit project roles for the `terrascale` tenant, as
[`platform-contract.md`](platform-contract.md) specifies (17 of its
operations). The caller presents its own sign-in access token; the
[port guide](csharp-port.md#platform-management-api-ts-598) has the design.
Fast tests drive every route over real HTTP on the real TerraBase as several
signed-in users: token refusal (none, identity, service, tampered, revoked
sign-in), outsiders seeing 404, every role gate, the last owner, project
names, collaborators, removal, paging, and two replicas racing owner
demotions and a grant against a removal. A fast test also checks that every
served platform operation matches the contract file. Invitations, ownership
transfer, audit and API keys are later slices; until invitations land,
members join only through the internal `AddMemberAsync` and `GrantAsync`.
