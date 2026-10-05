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
| Security primitives (Argon2id, AES-GCM, JWT/JWK, TOTP, WebAuthn) | TS-427 | Not yet ported |
| TerraBase Documents v1 persistence, uniqueness and single-use challenges | TS-489 (TS-428 design) | Implemented through ts-dotnet; create-only and ETag/If-Match CAS, deterministic scoped UUIDs, owner-checked release and retained consumed challenges. Local Release checks pass (100 fast, 21 slow, zero skips); live production activation gated on durability/recovery and TS-470 |
| Key ring, email, operator instances, rate limiting | TS-431 | Not yet ported |
| Hosted users, sessions, email links, password recovery, management users/keys | TS-490 (TS-432 design) | Not implemented; the generic storage boundary does not serve these flows |
| OAuth 2.0/OIDC provider on OpenIddict, device flow, guest accounts, CORS | TS-491 (TS-433 design) | Not implemented |
| MFA and email passcodes | TS-434 | Not yet ported |
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
exist in the configured database, then creates, reads and conditionally
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
