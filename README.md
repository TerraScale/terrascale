# @terrascale/sdk

The TypeScript SDK for native TerraBase documents and hosted TerraScale identity.
The package targets Node.js 22 and 24 and current Chrome, Firefox, Safari, and Edge.
Release qualification installs the exact packed artifact into a separate consumer.

```ts
import { createTerraBaseClient } from "@terrascale/sdk";

const database = createTerraBaseClient({
  tenant: process.env.TERRABASE_TENANT!,
  database: process.env.TERRABASE_DATABASE!,
  shard: BigInt(process.env.TERRABASE_SHARD!),
  apiKey: process.env.TERRABASE_API_KEY!,
});

const result = await database.read(collectionUuid, documentUuid);
if (!result.ok) throw new Error(result.error.code);
console.log(result.value.state);
```

Entity IDs are canonical UUIDs. Shard, generation, sequence, and signed integer
values remain bigint; bytes remain Uint8Array. Preserve every conflict sibling
and the complete causal context. Missing fields are omitted; `{ type: "null" }`
is an explicit null. Use the actual provisioned collection's numeric field IDs,
schema generation, and digest. Schema helpers describe local declarations and
SQL builders produce parameterized descriptors; neither provisions a catalog.

Native methods are `read`, `query`, `subscription`, `prepareCommit`, `commit`,
and `transactionStatus`. Queries use bounded typed predicates and opaque cursors.
Subscriptions first consume a complete paginated snapshot, then finite poll
groups. SQL execution requires the separate PostgreSQL listener contract.

`createDatabaseQuery(client, options)` delivers complete native snapshot and poll
cuts to `useQuery` from `@terrascale/sdk/react` or `createQuery` from
`@terrascale/sdk/svelte`. TanStack DB 0.9.2 integration is available through
`terrascaleCollectionOptions` from `@terrascale/sdk/tanstack`; map each complete
point explicitly and preserve conflicts. Publication is acknowledged before
advancing delivery, and cleanup cancels owned work. Mutations prepare and commit
through the same client; a presentation rollback does not establish a server abort.

Omitting `endpoint` uses authenticated, scope-bound routing at
`https://atlas.terrascale.tech` with the native `tbk1` database key. The client
selects an eligible canonical regional origin and checks its bounded lease again
before dispatch. `discoveryTimeoutMs` defaults to 3000 and accepts 1–30000 ms.
Each operation uses the receiver's highest-priority endpoint with the required
capabilities. Discovery denial, unavailability, invalid or expired leases, and
missing capabilities return a typed error before any native data request.
Set `endpoint` to use an independently provisioned direct HTTPS origin. HTTP is
allowed only on loopback for development, and the public test host is rejected.
Database keys and hosted identity management keys are separate credentials.

The admitted Atlas receiver advertises conditional mutations, atomic groups,
and security state as unavailable. Discovered clients refuse those operations
before native dispatch. Direct local native conditional semantics do not grant
those production capabilities.

For authentication/security conditional writes, use the provisioned single-writer authority, including
its generation and writer identity. An absent condition requires absence; a
version condition compares the complete point's state token. Copy the point's
full context into `observed` for replacement or deletion. Causal observation
alone does not establish compare-and-swap. Direct leaderless conditional commits
prove only the admitted local native exclusion semantics; they do not establish
distributed writer fencing. Leaderless storage is unsuitable for
security state and authentication invariants.

`prepareCommit` snapshots the request and computes its canonical SHA-256 effect
fingerprint. Reuse that prepared transaction on the same client for an exact
retry; a changed effect requires a new transaction identity. Mutations are not
automatically replayed. Resolve an uncertain mutation with transaction status;
unknown, pending, and expired status do not establish an abort. Persist the
transaction identity, authority, and fingerprint when recovery spans restarts.
`local_durable` and `object_durable` are distinct acknowledgement levels.

`@terrascale/sdk/ts-auth` contains the separate hosted identity management and
OIDC client. Its private transport preserves form-encoded token operations,
bounded responses, deadlines, and a single changed-token refresh. Configure
its own origin, instance, and identity credentials.

`@terrascale/sdk/local` is an explicitly enabled Node development runtime:

```ts
import { createTerraScaleSqliteRuntime } from "@terrascale/sdk/local";

const local = createTerraScaleSqliteRuntime({
  sqlite: { enabled: true, databasePath: "/absolute/path/development.sqlite" },
});
await local.bindCollection({
  collection: collectionUuid,
  schema_generation: 1n,
  schema_digest,
});
const client = local.createClient();
// Use local.authority for local conditional commits.
await local.close();
```

The local runtime refuses CI, production, qualification, staging, and deployment
environments. Its official Better Auth 1.7.2 adapter uses development SQLite
transactions and maintained adapter suites. These checks do not qualify
production security, regional replication, or the deployed customer application.

Management/catalog provisioning and credential lifecycle (C06/C07), production
Better Auth security persistence, and real installed producer/deployed account
journeys remain required release blockers. The management, administration, and
production Better Auth entrypoints fail with explicit capability errors until
those owning contracts are admitted. Candidate inspection does not authorize
publication.

```sh
npm ci
npm run typecheck
npm run build
npm test
npm run check:frameworks
npm run check:current-contract-inputs
npm run check:current-contract
npm run test:browser
```

Source parity requires absolute `TERRASCALE_CURRENT_OPENAPI` and
`TERRASCALE_ATOMIC_VECTORS` paths matching `sdk-current-contract.json` exactly.
Run `npm run qualify:current-producer` with a supplied immutable tarball and a
real isolated allocation; see [qualification inputs](scripts/qualification/README.md).
Missing real-producer inputs fail the required gate. See [release requirements](docs/release.md).

Hosted OAuth resource requests use one exact registered HTTPS `resource`. `oidc.authorizationUrl`, `oidc.deviceAuthorization`, and the code, device, refresh, and client-credentials token grants retain its bytes, including path and query. Omission on a browser code or refresh flow preserves the stored no-resource binding; it never repairs a rejected audience or falls back to browser login. Refresh requests can ask for narrower scopes. Client credentials require explicit resource and scope values. Token and originating-client introspection methods use `client_secret_post`; public-client and originating-client Basic token authentication are not currently exposed by this SDK.

`oauthClients` and `oauthResources` provide the producer's snake_case policy records and named management operations. Resource PATCH requires `expected_auth_epoch`; client PATCH accepts it. JSON epochs outside JavaScript's exact integer range are rejected. Client PATCH returns a bare client view unless `rotate_secret` is true, when it returns the client and its one-time secret. Resource create/PATCH always return the resource and nullable one-time introspection secret.

Use `oidc.introspectResource` with independently configured `issuer`, exact `audience`, `introspection_client_id`, and `introspection_secret`. It uses resource Basic authentication only, with each credential component form encoded before Base64. An active result requires the exact configured issuer/audience, `Bearer`, integer timestamps valid at the local clock with no default skew, and the required user/service session shape. Inactive results contain only `active: false`. Resource results expose no internal epochs, refresh family, or token-use fields. The provider checks current durable token/session/resource state; an active principal or scope does not grant project ownership or management authority. Product authorization remains at the caller's existing authorization boundary.

Regional native database origins use `https://<region>.api.terrascale.tech`,
for example `https://gru.api.terrascale.tech`. The global API host and retired
root regional hosts cannot serve as native origins. Auth uses its separate
`.auth.terrascale.tech` namespace. Direct independent origins must use ASCII
hostnames; percent-encoded and Unicode hostname aliases are rejected.
