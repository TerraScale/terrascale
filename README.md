# TerraScale Go SDK (`terrascale.tech/sdk`)

Package `v1` is the TerraBase client: documents, collections, queries and
transactions with an API key, and the Management API for the dashboard
backend. The root package holds the product clients, hosted TSAuth clients and
the pure schema and SQL builders.

## TerraBase v1 client (`terrascale.tech/sdk/v1`)

Package `v1` talks to the C# TerraBase server: the Documents API v1 and the
Management API (terrabase `docs/decisions/0004-document-api-v1.md` and
`docs/management-api.md`). A database is addressed by its project id and name;
the API key's organization selects the tenant.

```go
import tb "terrascale.tech/sdk/v1"

c, err := tb.New(tb.Config{BaseURL: "https://gru.terrabase.example", Token: apiKey}) // tb_…
db := c.Database(projectID, "main")
_, err = db.Create(ctx)                     // PUT; Created reports a 201
_, err = db.CreateCollection(ctx, "users") // 409 name_taken when it exists
cols, err := db.Collections(ctx, tb.ListOptions{Limit: 50})
users := db.Collection("users")             // also Info and Drop

res, err := users.Create(ctx, map[string]any{"email": "ana@example.com"}) // server UUIDv7
doc, err := users.Get(ctx, res.ID)
var body map[string]any
err = doc.Decode(&body) // numbers decode as json.Number, never float64

id := tb.DocumentIDFor("users", "ana@example.com") // deterministic UUIDv5 natural key
_, err = users.Put(ctx, id, body, tb.IfNoneMatchAny())
_, err = users.Patch(ctx, id, tb.Ops(tb.Inc("/logins", 1), tb.Append("/tags", "beta")), tb.IfMatch(doc.Version))
_, err = users.Patch(ctx, id, tb.MergePatch(map[string]any{"legacy": nil}))
_, err = users.Delete(ctx, id, tb.IfMatch(version))
page, err := users.List(ctx, tb.ListOptions{Cursor: next}) // id order
```

Every call resolves its endpoint through `tb.Resolver`. `BaseURL` wraps a
`tb.StaticResolver`; alternatively, set `Config.Resolver` to a concurrent-safe
resolver implementing `Resolve(context.Context) (string, error)`. Set one of
`BaseURL` or `Resolver`. Resolved URLs are validated before credentials are
sent, resolution errors stop the call, and retries keep the original endpoint.
The same configuration applies to `NewManagement`. Atlas resolution follows
in TS-507; no production endpoint is built into the SDK.

Every write returns `WriteResult{ID, Version, Offset, Durability}` and the
version is the `ETag`. Errors are `*tb.APIError{Status, Code, Message, Op,
RetryAfter, ETag}`, and every error code is also an error value for
`errors.Is`, for example `tb.ErrNotFound`, `tb.ErrPreconditionFailed`,
`tb.ErrUnknownDatabase` or `tb.ErrNameTaken`. A 404 for a deleted document
carries the tombstone version in `ETag`, which recreates it with `IfMatch`.

Retries: TerraBase does not deduplicate retries and rejects `Idempotency-Key`,
so the client never sends one. Only requests a replay cannot apply twice are
retried: reads, queries, `PUT` of a database and writes with an `IfMatch`
version (a transaction when every write op has one). `IfNoneMatchAny` is not
enough, since a tombstone left by another writer satisfies it again. For
those, 429 is retried after `Retry-After`, and the transient rejections
(`capacity`, `storage_unavailable`, `dependencies_unavailable`) with backoff.
Transport errors and `commit_uncertain` are retried only for reads and
queries. Mutations return uncertain outcomes without a replay: use
`errors.Is(err, tb.ErrCommitUncertain)` and re-read the state before deciding
what to do. A transport failure matches that code and retains its original
cause; a server response retains its `*tb.APIError`. Other writes and
`500 internal_error` are never retried. Set `Config.MaxRetries` to change the
budget. Path segments containing `/` fail locally: TerraBase would match an
escaped `%2F` literally.

Transactions apply up to 1024 ops atomically; failures name the op index:

```go
res, err := db.Tx().
	Create("users", "", map[string]any{"email": "bia@example.com"}).
	Patch("projects", projectID, tb.Ops(tb.Inc("/db_count", 1)), tb.IfMatch(v)).
	Assert("plans", planID, tb.Eq("/visible", true)).
	AssertAbsent("invites", inviteID).
	Commit(ctx)
var apiErr *tb.APIError
if errors.As(err, &apiErr) {
	op, _ := apiErr.OpIndex() // failing op
}
```

Queries use filter constructors (`And`, `Or`, `Not`, `Eq`, `Ne`, `Lt`, `Lte`,
`Gt`, `Gte`, `In`, `Nin`, `Exists`, `Prefix`, `Contains`), paths as JSON
Pointers or `tb.IDPath`, and page with keyset cursors:

```go
q := tb.Query{Filter: tb.And(tb.Eq("/org_id", org), tb.Gte("/created_at", json.Number("1759363200000"))),
	Sort: []tb.SortKey{tb.Desc("/created_at")}, Project: []string{"/email"}, Limit: 50, Count: true}
for doc, err := range users.Items(ctx, q) { … }     // every match across pages
for page, err := range users.Pages(ctx, q) { … }    // page.NextCursor, page.Count
```

### Management API

The ts-dashboard backend manages organizations, projects, databases, members,
invitations, ownership transfers and the audit log with the dashboard service
credential. `As` acts as one ts-auth user: it sends `X-TerraBase-User-Id` on
every call and `X-TerraBase-Organization-Id` on organization routes. Take the
user id from the session, never from browser input.

```go
m, err := tb.NewManagement(tb.Config{BaseURL: origin, Token: dashboardCredential})
u := m.As(session.UserID)
org, err := u.CreateOrganization(ctx, "Acme")
project, err := u.CreateProject(ctx, org.ID, "Production")
db, err := u.CreateDatabase(ctx, org.ID, project.ID, "main") // then c.Database(project.ID, "main")
inv, err := u.CreateInvitation(ctx, org.ID, "bia@example.com", tb.RoleAdmin)
page, err := u.Members(ctx, org.ID, tb.PageOptions{After: next})
```

It also covers `Organizations`, `RenameOrganization`, `SetPlan`,
`DeleteOrganization`, `CurrentOrganization`/`SetCurrentOrganization`, project
and database reads, renames and deletes, `BranchDatabase`, `SetMemberRole`,
`RemoveMember`, `Leave`, `Invitations`, `RevokeInvitation`,
`AcceptInvitation`, `OwnershipTransfer` and its start, cancel and confirm,
`AuditRetention`/`SetAuditRetention` and `AuditEvents`. A 404 (`ErrUnknownRoute`)
means an unknown id or an organization the user is not a member of; bare
message errors map to `ErrInvalidRequest` (400), `ErrForbidden` (403, role too
low) and `ErrConflict` (409).

Not served by TerraBase yet, so not in the client: shapes (reactive queries),
query `explain`, `Idempotency-Key`, and the `/v1/dashboard` control plane.

### Tests

Tests of the TerraBase client run against the real TerraBase release, never a
fake server. The importable `terrascale.tech/sdk/terrabasetest` package is the
shared harness for SDK and downstream tests: it pins the release
by version and SHA-256, downloads it once with `TERRABASE_TOKEN` (read access
to TerraScale/terrabase) into the user cache directory (or `TERRABASE_CACHE`),
and starts it per test with a generated TLS certificate and dashboard
credential file. TerraBase assigns both listener ports. Startup is bounded to
30 seconds; admin requests, including their response bodies, are bounded to
10 seconds.

```sh
TERRABASE_TOKEN=… TERRABASE_REQUIRE=1 nice -n 19 go test ./v1/ ./terrabasetest/
```

`v1/integration_test.go` exercises every SDK call against it. Its recording
transport checks the real traffic: each request is a route of
`v1/testdata/openapi.json` (the executable's own `/openapi/v1.json`, admin port
9464) and each of those routes is called, no request carries
`Idempotency-Key`, and the Management headers are present. Retry tests inject
faults into that transport in front of the real server. Tests skip only on an
unsupported platform or when neither a cached executable nor a token is
available; CI sets `TERRABASE_REQUIRE=1` to fail instead. Checksum, download
and filesystem errors always fail, including in local runs.

Downstream tests call `terrabasetest.Start(t)` to get a fresh real server.
Use its `URL`, `Credential`, and trusted `Client` with `v1.NewManagement`
to create an organization and project, then `Server.APIKey` to issue a
project-scoped document credential. Pass `Server.Client` as `Config.HTTPClient`
so SDK clients trust the generated TLS certificate. The process and temporary
data, TLS, and credential files are cleaned up automatically when the test ends.

[`terrabasetest/example_test.go`](terrabasetest/example_test.go) is a runnable
downstream restart example: it writes through one store, creates a fresh store
against the same running database, and checks the persisted document and version.
Keep `Start(t)` outside the app/store restart; calling it again creates a new,
empty database server. Copy the test into a downstream package and replace the
SDK database handle with the application's store constructor.

Consume this package from the SDK module's merged Go pseudo-version; this repo
has no tag or release-on-merge workflow. The TerraBase executable version and
SHA-256 remain pinned by the helper, independently of the SDK module version.

## Product, TSAuth, and builders

Product calls require an explicit `APIBaseURL`. `AccessToken` and
`TokenProvider` configure their management credential. `ProjectAPIKeys`
uses the authenticated Dashboard project API-key routes. The routes require
the owning session and project membership.

TSAuth calls require an explicit `TSAuthBaseURL`, or a suitable explicit
`APIBaseURL`. Use `client.TSAuth.Instance("slug")` for the scoped families.
Management methods use the instance credential. The OIDC application API uses
explicit issuer and OAuth client authentication, verifies authorization-code
responses and ID tokens, and exposes introspection, revocation, and logout
flows. Accepted OIDC source
checks do not qualify an actual hosted-provider journey.

Schema and SQL builders construct and validate statements locally and do not
execute SQL over HTTP. The explicit pgx-backed PostgreSQL client uses separate
listener configuration, credentials, and CA trust. Actual PostgreSQL execution
requires its producer protocol and an admitted signed listener; source and
package checks do not qualify an actual admitted PostgreSQL runtime.
Retained product methods do not establish management, provisioning, or restore
runtime qualification.

## Checks

The edge artifacts and pins are in `contracts/edge/v1`.

```sh
test -z "$(gofmt -l .)"
go vet ./...
go build ./...
go test -race ./...
```

The module requires Go 1.25 and has no CGO dependency. CI runs the pinned
vulnerability scanner alongside formatting, vet, build and race tests,
including the v1 integration tests against the pinned TerraBase. BLAKE3
remains a dependency of the retained product artifact verification.

## Resource OAuth

A registered resource uses its own introspection credentials. Originating OAuth
clients obtain opaque access tokens for one exact HTTPS resource URI. The URI's
raw path and query remain part of its identity. Management keys register policy;
they do not substitute for either OAuth credential role.

```go
origin, err := instance.NewResourceClient(sdk.TSAuthResourceClientConfig{
    Issuer: "https://auth.terrascale.tech/example",
    ResourceAudience: "https://api.example.test/management",
    Authentication: sdk.TSAuthOAuthClientAuthentication{
        ClientID: originClientID, ClientSecret: originSecret,
        Method: sdk.TSAuthClientSecretBasic,
    },
})
if err != nil { return err }
grant, err := origin.RequestClientCredentials(ctx, []string{"management:read"})
if err != nil { return err }
resource, err := instance.NewResourceIntrospector(sdk.TSAuthResourceIntrospectorConfig{
    Issuer: "https://auth.terrascale.tech/example",
    ResourceAudience: "https://api.example.test/management",
    Authentication: sdk.TSAuthResourceAuthentication{
        ClientID: introspectionClientID, Secret: introspectionSecret,
    },
})
if err != nil { return err }
principal, err := resource.Introspect(ctx, grant.Tokens.AccessToken)
if err != nil { return err }
// Authorize principal against current durable grants in the application.
```

`OAuthClientPolicies()` and `OAuthResourcePolicies()` expose explicit allowlists,
checked numeric `uint64` epochs, secret rotation and disable operations. Secrets
are returned once. Nonrotating client PATCH returns a client record; a rotating
PATCH returns the client plus its new secret. Resource PATCH always returns a
resource record and a nullable secret.

`NewResourceApplication` adds resource selection to verified browser OIDC while
retaining server-held state, nonce and PKCE. The application atomically consumes
an attempt before exchanging its callback. ID-token audience remains the
originating client. Device authorization exposes one explicit poll at a time and
honors the provider interval. Code, device and refresh redemption can inherit
or repeat their bound URI; they cannot select another resource. Refresh scope
`nil` inherits the current grant; a nonnil empty slice narrows it to no scopes.

Serving-resource introspection requires Basic authentication and validates the
configured issuer, exact audience, Bearer type, integer times and principal shape.
Users require a nonempty session ID. Services require null session ID and subject
matching originating client ID. Inactive results grant no access. Subject,
scopes and activity do not establish project or provisioner ownership; current
durable grants remain application authorization. Introspection uses a fresh
request with no browser cookie jar or management/native token provider.

Unknown exchanges and refresh rotations return `TSAuthOIDCError.Uncertain` and
are never automatically replayed. Resource contracts pin the independently
reviewed protocol source and actual loopback HTTP response captures. Those
fixtures verify source interoperability; native persistence, two-process
revocation and hosted caller qualification have separate evidence.
