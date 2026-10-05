# C# port (TS-424)

ts-auth is a C# service on .NET 10. Epic TS-424 (ku.mariogk.com) tracks the
port. The Rust workspace was removed in TS-442; its source in Git history
before `8dd2874` is the behavioural reference for each port stream, including
the original TS-428 storage design. TS-489 implements the Documents v1 storage
boundary. TS-490 adds users, sessions and email passcodes as internal services
that no route serves yet; OIDC (TS-491) is not implemented. `docs/parity.md`
tracks what has landed.

## Layout

| Path | Contents |
|---|---|
| `src/TerraScale.Auth/` | ASP.NET Core host. Features live in `Features/<Area>/`; each endpoint is one TerraScale.Endpoints class. |
| `src/TerraScale.Auth.Contracts/` | Persisted models and the `{"error":{"code","message"}}` envelope (`ApiException`). |
| `src/TerraScale.Auth.Security/` | Cryptographic primitives (port of `auth-security`): Argon2id, the secret box, ES256 keys and JWTs, tokens. |
| `src/TerraScale.Auth.Store/` | Documents v1 persistence and storage readiness through ts-dotnet (TS-489, from the TS-428 design). |
| `tests/TerraScale.Auth.Fast/` | Blocking integration suite (TUnit + Playwright). |
| `tests/TerraScale.Auth.Slow/` | Non-blocking integration suite. |
| `tests/Shared/` | Harness source linked into both suites: loopback Kestrel app, Chromium launcher, pinned real TerraBase fixture and observability test doubles. |

There are no unit tests. HTTP tests start the real application with
`AuthTestServer.StartAsync`. Storage tests use ts-dotnet against the real
TerraBase binary; `TerraBaseFixture` shares one process per suite and creates
isolated synthetic tenants/databases. Observability doubles test scheduling
and error reporting, not the persistence boundary.

The sibling `ts-dotnet` and `ts-libs` checkouts are project references. Locally
they are found at `../ts-dotnet` and `../ts-libs`, or under
`~/Projects/TerraScale/`; pass `-p:TerraScaleRoot=<dir>` to use another
location. CI checks out the revisions pinned in `.forgejo/workflows/ci.yml`
into a job-owned directory outside the repository.

Both `.forgejo/workflows/ci.yml` and `.forgejo/workflows/slow.yml` pin ts-dotnet
to `bdea1e8f4926e46367b751d8f633dcec04d760e9` (SDK merge #24). That revision's
`src/TerraScale.DotNet.Testing/TerraBaseRelease.cs` pins the Linux x64 server:

| Pin | Value |
|---|---|
| TerraBase commit | `ff256067015433c8c40b59879f1282d2a40480ce` |
| Release tag | `ci-ff256067015433c8c40b59879f1282d2a40480ce-run-13258` |
| Binary SHA-256 | `e07d173fc2bbab19dcb1fde8b121b0207b07d55452a5b19a406871d220e05e13` |

The helper verifies the binary's SHA-256 on every start. Provide a matching
`TERRABASE_BINARY`, a verified cached binary, or `TERRABASE_DOWNLOAD_TOKEN` /
`ORG_READ_TOKEN` to download the private release. `TERRABASE_CACHE_DIRECTORY`
overrides the cache location. The ts-auth fixture fails when the binary is
unavailable, including locally; it never skips these tests. The SDK's
`docs/terrabase-consumers.md` describes the adoption procedure but reflects
ts-auth before TS-489; its older pins and local-skip example do not describe
this integration. Local Release validation passed: formatting, build, locked
restore, NuGet audit, OpenAPI lint, 100 fast tests and 21 slow tests with zero
skips. No production/live acceptance is claimed.

## Commands

Run them at low CPU priority locally:

```sh
nice -n 19 make check       # format, build (analyzers as errors), NuGet audit, OpenAPI lint, fast suite
nice -n 19 make test-slow   # slow suite
nice -n 19 make dev         # dotnet watch, published over HTTPS on the tailnet
```

`make dev` picks a free loopback port and a free tailnet HTTPS port for this
worktree, prints the `https://<machine>.<tailnet>.ts.net:<port>` origin, and
removes only its own `tailscale serve` mapping when it stops. Put local
settings in `.env` (gitignored); without it, throwaway Development values are used.

## Configuration changes from the Rust service

The release image is the self-contained executable on the chiseled
`runtime-deps` base (see README, CI).


Configuration retains the `TS_AUTH_*` convention, with these changes:

- **Storage selectors and credentials:** `TS_AUTH_DATABASE` is required as
  `project-uuid/database`. The intended production dataset is `auth/auth` in
  organization `terrascale-internal`, where the first `auth` is the project
  label. The pinned server and inspected TerraBase `origin/main` resolve that
  route by project ID, not label: configure `<auth-project-uuid>/auth`.
  `AuthOptions` requires a lowercase hyphenated project UUID and a lowercase
  database name of at most 63 characters. An operator resolves the project
  label to its UUID through Management before deployment; runtime does no
  Management lookup. The API key is an organization-bound `tb_…`
  credential with `documents:read` and `documents:write`, narrowed to the
  project/database where appropriate. Runtime uses no management credential.
  Separate project/tenant selector options have been removed.
- **Storage endpoint:** `TS_AUTH_TERRASCALE_API_URL` is required, with no
  default. It must be an HTTPS origin without credentials, path, query or
  fragment, on `api.terrascale.tech` or a host under `.api.terrascale.tech`;
  loopback HTTPS is allowed only in Development. `App.Build` registers this
  as an explicit `TerraBaseStaticEndpointResolver` override through
  `ITerraBaseEndpointResolver`. The host does not hardcode GRU routing.
  Atlas discovery in ts-dotnet belongs to TS-507; the injectable resolver
  allows that implementation to replace the static override.
- **`GET /metrics` and `TS_AUTH_METRICS_KEY` are removed.** This is an
  intentional breaking change. Metrics, traces and logs are pushed over
  OpenTelemetry OTLP when the standard `OTEL_EXPORTER_OTLP_ENDPOINT` (and
  optionally `OTEL_SERVICE_NAME`, `OTEL_EXPORTER_OTLP_HEADERS`) is set. A
  collector can expose a Prometheus scrape endpoint if one is needed.
- **`TS_AUTH_SENTRY_DSN`** enables error reporting to GlitchTip through the
  Sentry SDK, for errors only. It is a deployment secret and must never be
  committed. Events are scrubbed before they leave the process: only
  non-sensitive request headers are kept; cookies, query strings, bodies, the
  user and the client IP are dropped; sensitive keys in tags, extras and
  breadcrumbs are masked. The fast suite checks this against a fake collector.
- Framework request logging (`Microsoft.AspNetCore.Hosting.Diagnostics`,
  `System.Net.Http.HttpClient`) is limited to warnings, because those
  categories log full URLs, query strings included.

## Storage and readiness (TS-489)

`AuthStore` uses generic per-feature collections through
`TerraBaseDocumentsClient`, with no unconditional writes or automatic write
retries. Reads expose the opaque ETag version; create uses
`TerraBasePrecondition.Absent` (`If-None-Match: *`), and replacement/deletion
uses `Matches(version)` (`If-Match`). Callers authorize the instance before
entering this internal boundary. Missing databases/collections and outages
propagate; there is no in-memory fallback.
Generic create/update/delete reject `uniques`, `verification_tokens` and
`storage_health`, so feature callers cannot bypass their dedicated operations.

Natural-key addresses are deterministic UUIDv8 values derived from SHA-256
over length-framed instance/kind/key inputs; new TerraBase IDs must be UUIDs.
Email normalization trims and lowercases invariantly; email syntax validation
belongs to the account boundary. Uniqueness uses create-only claim documents,
and explicit release checks the stored instance, kind, owner and version.
Never compensate an ambiguous write by releasing its claim; retain uncertain
ownership until writers are quiesced and an operator can verify it before a
conditional release.

Single-use challenges persist only lowercase SHA-256 secret hashes in
`verification_tokens`, with instance, purpose, subject and expiry bindings.
Consumption rejects invalid bindings, expiry and prior consumption, then
marks the record consumed with CAS and returns `Consumed=true` on success.
The record remains to reject replay and reissue; a write completing after
expiry cannot grant authorization.

`TerraBaseStorageHealth` checks that every collection in
`AuthStore.RequiredCollections` exists (`uniques`, `verification_tokens`,
`storage_health`, and since TS-490 `users`, `sessions` and `passcodes`), then performs a
synthetic create-only write, version/content-checked read and conditional
delete in `storage_health`. `/readyz` returns 200 only on success and 503 on
missing state, permission failure, outage or timeout. Concurrent observations
coalesce; the latest completed result is cached for one second and the
deadline is two seconds. `region` is omitted when unknown, as it is for this
probe.

The application and probe never provision or repair state. An operator/test
can explicitly call `AuthStore.ProvisionAsync` to create those three
infrastructure collections, plus `users`, `sessions` and `passcodes` (TS-490),
in an already existing database; repeated setup accepts an existing collection.
Later feature streams must provision their own collections. Readiness requires
the same list, so a database provisioned before TS-490 reports 503 until
`ProvisionAsync` runs again. This is not production project/database provisioning or a migration of
Rust documents.

## Users, sessions and passcodes (TS-490)

`Users`, `Sessions` and `Passcodes` in `src/TerraScale.Auth/Features/Identity/`
are singletons registered in `App.Build` over `AuthStore`. No hosted page, JSON
route or management API serves them yet: routes need instance resolution and the
email sender from TS-488 (PR #32, not merged). Callers authorize the instance
before using them. The fast suite drives sign-in, rotation and sign-out over real
HTTP through test-only routes (`tests/TerraScale.Auth.Fast/Identity/`).

- **Users** (`users`): creating an account writes the email claim and the user
  document in one TerraBase transaction, both create-only, so a claim never
  outlives a failed creation. A taken email returns null. Emails are trimmed,
  lowercased and checked for plausibility (one `@`, no whitespace or control
  characters, at least two non-empty domain labels).
- **Sessions** (`sessions`): the 48-character token is never stored; its
  SHA-256 is the document address. The lifetime is an absolute 14 days. Each
  resolution reads TerraBase and checks instance, revoked flag, expiry and the
  user's `authEpoch`, so revocation is immediate on every replica. Rotation
  revokes the old session with a conditional write before issuing a successor
  with the same expiry. Sign-out everywhere advances `authEpoch`. Every ten
  minutes each replica's `SessionPruner` deletes revoked and expired documents
  with version-conditional deletes in batches of 500, continuing while a batch
  comes back full and deletes something; competing replicas share the backlog.
  A dead session grants nothing while it waits. A failed pass logs a warning and
  retries.
- **Cookie**: the existing `tsauth_session` from `BrowserSecurity` (`Secure`,
  `HttpOnly`, `SameSite=Lax`, `Path=/`, browser-session lifetime).
- **Passcodes** (`passcodes`, one document per user): six digits, 10 minutes,
  single use, 5 wrong guesses, 5 issued codes per user per hour. Only an
  HMAC-SHA256 keyed by HKDF-SHA256 of `TS_AUTH_MASTER_KEY` and bound to instance
  and user is stored, so changing the master key invalidates outstanding codes.
  Each wrong guess or redemption is a conditional write that must win, so
  concurrent requests cannot exceed the limit or redeem a code twice. A code is
  consumed before its session is written.

Per-user limits live on the passcode document. Per-client and per-route limits,
generic responses and timing equalization belong to the routes that will expose
these flows, with the TS-488 `AttemptLimiter`, which is not on `main`. Rust
stored SHA-256 of the code and throttled resends to one per 30 seconds; C# uses
the keyed HMAC and the hourly issue cap instead. Password sign-in and reset,
magic links, email verification, account pages and the management API remain in
TS-432's later work.

## Production activation gate

[`scripts/provision.sh`](../scripts/provision.sh) prints only a provisioning
plan. It performs no live setup; production execution belongs to TS-470.
Every auth replica must use the same single configured storage authority,
without automatic write retries or failover to another copy. Atlas endpoint
discovery (TS-507) does not supply replication or failover guarantees.

The pinned server's [decision 0003](https://git.terrascale.tech/TerraScale/terrabase/src/commit/ff256067015433c8c40b59879f1282d2a40480ce/docs/decisions/0003-sqlite-synchronous-normal.md)
uses SQLite WAL with `synchronous=NORMAL`. A TerraBase process crash alone
preserves committed writes, but an OS crash or power loss can roll back
acknowledged PUT/DELETE transactions since the WAL was last synced. Thus
uniqueness claims and consumed-challenge records can be rolled back.
[Decision 0005](https://git.terrascale.tech/TerraScale/terrabase/src/commit/ff256067015433c8c40b59879f1282d2a40480ce/docs/decisions/0005-multi-tenancy.md)
states that replication `Sync` is rejected by a bootstrap stub and committed
batches do not yet carry database identity. There is no supported replication
or regional failover for this pin.

Storage is implemented, but these limits leave live production activation
gated on an approved durability/recovery design and production verification
under TS-470. `/readyz` establishes current read/conditional-write access,
not crash durability, replication or safe failover. Local integration tests
cannot establish those guarantees.

## Key ring and attempt limits (TS-488)

The key ring and the attempt limiter store their state through
`IDocumentStore` (`src/TerraScale.Auth.Store/DocumentStore.cs`), addressed by
collection, instance and key: read with a version, create-only, and
replace-if-version. Production uses `TerraBaseDocumentStore`, which maps
these onto `AuthStore` natural-key addresses with `If-None-Match: *` and
`If-Match`. Single-use, uniqueness and counter state must be built on those
conditional writes, never on a read followed by an unconditional write or on
process locks. Consumers wrap the store in `FailClosedDocumentStore`, so any
failure other than caller cancellation (an outage, a missing collection, a
rejected key, an unexpected API error) answers 503 with the original
exception kept for server logs. The fast and slow suites run both against the
real SDK-pinned TerraBase; test-only wrappers inject failures and contention
over that real store, and two servers bound to one database stand in for
replicas.

Their collections, `signing_key_rings` and `rate_limits`, are provisioned
with the other collections (`scripts/provision.sh`); while one is missing the
feature answers 503.

Signing keys live in one document per instance, so a rotation is one
conditional replace. Each key's d ‖ X ‖ Y is sealed under `TS_AUTH_MASTER_KEY`
with `signing-key:<kid>:<instance>` as associated data, so a key copied into
another instance's ring fails to open. Rotation adds a key and keeps the older
ones validating. Lookup and rotation validate the complete original ring's
count, valid Unix timestamps and strict oldest-first creation order before
importing keys; rotation restores every original key before the capacity
check or any write. A malformed or unreadable ring fails closed and stays
unchanged. Creation timestamps are kept strictly increasing across replicas,
but this ordering does not establish supersession age.

Automatic retirement is disabled: the pinned Documents v1 SDK exposes only
document content and an opaque version, without an authoritative update or
supersession time. A key created 72 hours ago can still sign a fresh token;
a replica 49 hours slow can date its successor 49 hours ago, making the old
48-hour pruning rule remove a live token's key on the next accurate rotation.
JWTs live at most 24 hours (`Jwt.MaxLifetime`), but replica wall clocks alone
cannot prove expiry. Rotation retains all keys and answers 409
`signing_key_limit` at 16 keys, regardless of elapsed local time; signing and
validation continue with the unchanged ring. There is no automatic recovery
from that capacity limit. Retirement remains gated on an approved design
with trustworthy time and safe handling of concurrent/in-flight signing;
do not delete keys or weaken the bound to bypass it. The persisted schema is
unchanged. Fast regressions cover the old-active-key/fresh-token slow-then-
accurate rotations, capacity after a clock jump, malformed/oversized rings,
timestamp limits and a corrupted oldest inactive key.

Attempt buckets are keyed by an HMAC of purpose and subject under a key
derived from the master key, within the instance's address space, and one
document is kept per distinct subject.

## OpenAPI

TerraScale.Endpoints serves the document at `/openapi/v1.json` and Scalar at
`/scalar`. Scalar's JavaScript is served from the package, and its default
fonts are turned off, so the page makes no third-party requests. The document
transformers in `Hosting/OpenApiSetup.cs`:

- set the server to the configured public origin, never the request's Host header;
- apply the security scheme each route enforces, and mark public operations `security: [{}]`;
- define only the schemes that are actually used;
- require a description for every tag.

`nice -n 19 make openapi-check` lints the served document with Redocly locally.
