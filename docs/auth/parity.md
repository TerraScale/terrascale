# C# port capability tracker

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
| Telemetry (OTLP) and GlitchTip error reporting | TS-426 | Served; fast tests assert that no secret reaches GlitchTip or any log provider |
| Security primitives (Argon2id, AES-GCM, JWT/JWK, TOTP, WebAuthn) | TS-427 | Partly ported (TS-488): Argon2id via libsodium (PHC, m=19456, t=2, p=1; verification refuses hashes above 64 MiB or above the memory × passes cost of the RFC 9106 64 MiB, t=3 profile), the AES-256-GCM secret box under the master key with record-bound associated data, random tokens, SHA-256, HMAC and fixed-time comparison, ES256 keys, public JWKs and JWT signing and validation (Microsoft.IdentityModel, ES256 only; kid, issuer, exact audience, `typ` and expiry enforced). Token lifetimes are capped at 24 hours; Rust JWT templates allowed any lifetime, so the template port (TS-439) adopts the cap. Fast tests cover reference vectors, malformed hashes, tampering, unknown or missing kids, ES384 and HS256 headers, and wrong `typ`, audience or lifetime. Not ported: importing untrusted JWKs, RS256 and kid-less verification for upstream OIDC (TS-495 federation), HS256 keys, PKCE (TS-491), TOTP and WebAuthn (TS-495) |
| TerraBase Documents v1 persistence, uniqueness and single-use challenges | TS-489 (TS-428 design) | Implemented through ts-dotnet; create-only and ETag/If-Match CAS, deterministic scoped UUIDs, owner-checked release and retained consumed challenges. Local Release checks pass (100 fast, 21 slow, zero skips); live production activation gated on durability/recovery and TS-470 |
| Key ring, email, operator instances, rate limiting | TS-431 | Partly ported (TS-488): per-instance ES256 key ring with rotation (every key retained; at most 16 keys, further rotations answer 409 regardless of elapsed replica time; automatic retirement disabled until an approved trustworthy-time design exists), the shared attempt limiter (one-minute windows), SMTP with mandatory STARTTLS and the logging fallback; observability landed in TS-426. Lookup and rotation validate the complete original ring's count, valid timestamps and strict creation order; rotation restores every original key before checking capacity or writing. Malformed, oversized and unreadable rings fail closed unchanged. The key ring and limiter use conditional writes on the TS-489 store (`signing_key_rings`, `rate_limits` collections); any storage failure other than caller cancellation answers 503. Fast and slow tests run against the real pinned TerraBase, including two replicas racing. Limiter bucket ids are HMAC-keyed from the master key; callers pass identifiers the service already holds, and a caller limiting by attacker-chosen text (Rust's guest-mailbox folding) ports with that caller. Without SMTP outside Development, email is logged and a startup warning is emitted; whether flows that need delivery must refuse to start is decided with the first flow (TS-490). Operator instances, the management-key gate (with management keys, TS-490) and Data Protection keys in TerraBase are not yet ported |
| Users, sessions and email passcodes (internal services) | TS-490 (TS-432 design) | Implemented on the TerraBase store and registered in DI; no route serves them yet (see below) |
| Hosted sign-in pages, email links, password sign-in and recovery, management users/sessions/keys | TS-432 design (not in TS-490) | Not implemented |
| OAuth 2.0/OIDC provider on OpenIddict, device flow, guest accounts, CORS | TS-491 (TS-433 design) | Not implemented |
| MFA (TOTP, recovery codes, step-up) | TS-434 | Not yet ported; email passcodes moved to TS-490 |
| Passkeys | TS-435 | Not yet ported |
| OIDC, GitHub and SAML federation | TS-436 | Not yet ported |
| Organizations, invitations, domains, roles and authorization checks | TS-437 | Not yet ported |
| SCIM directories, users, groups (including operator repair runbooks) | TS-438 | Not yet ported |
| JWT templates, webhooks, signed audit log | TS-439 | Not yet ported |
| Billing, quota seats, Stripe webhooks | TS-440 | Not yet ported |
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

Runtime selects `<auth-project-uuid>/auth`, with the production project
labelled `auth` in organization `terrascale-internal` (logical dataset
`auth/auth`). An operator resolves that UUID through Management before
deployment; runtime uses only a `tb_…` Documents credential scoped for
`documents:read` and `documents:write`. The required HTTPS endpoint is an
explicit static override via `ITerraBaseEndpointResolver`; Atlas SDK discovery
belongs to TS-507. `scripts/provision.sh` prints a plan only; live execution
belongs to TS-470.

The pinned server's decisions 0003 and 0005 limit deployment: SQLite WAL
`synchronous=NORMAL` can lose acknowledged writes since the last WAL sync
after an OS crash or power loss, though a process crash alone preserves them;
replication `Sync` is unsupported. All replicas must use a single authority,
with no automatic retries or failover. Lost claim/consumption writes can
invalidate security guarantees. Live production activation remains gated on
an approved durability/recovery design and TS-470 verification; readiness
does not prove production durability. The [port guide's activation gate](csharp-port.md#production-activation-gate)
links the pinned decisions.

Persistent uploaded files are not implemented; admin assets and fonts are
embedded in the executable. If persistent files are added, they use RustFS
through S3.

## TS-490 users, sessions and email passcodes

Users, server-side sessions and email passcodes are internal services in
`src/TerraScale.Auth/Features/Identity/`, registered in DI on the TerraBase
store. No hosted page, JSON route or management API serves them yet: routes need
instance resolution and the email sender from TS-488 (PR #32, not merged). The
fast suite drives sign-in, rotation and sign-out over real HTTP through
test-only routes.

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
10 minutes, work once and die after 5 wrong guesses. At most 5 codes are issued
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

Per-user limits live on the passcode document. The TS-488 `AttemptLimiter` is not
on `main`; per-client and per-route limits, generic responses and timing
equalization belong to the routes that will expose these flows.

`AuthStore.ProvisionAsync` now also creates `users`, `sessions` and
`passcodes`, and `/readyz` requires them: a database provisioned before TS-490
reports 503 until an operator runs `ProvisionAsync` again.

Not in TS-490 (remaining TS-432 surface): hosted sign-in and sign-up pages,
password sign-in and reset, magic links, email verification, account and
session-list pages, the management API for users, sessions, impersonation,
instances and API keys.
