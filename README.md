# @terrascale/sdk

The TypeScript customer SDK for TerraBase documents and TerraScale authentication.
The current TerraBase client uses the producer's scoped `/v1` document protocol:
canonical UUID tenant/database/collection/document IDs, bigint shard and schema
generations, exact typed values, complete causal reads, atomic conditional commits,
and durable transaction status.

The package targets Node.js 22 and 24 and the current stable major versions of
Chrome, Firefox, Safari, and Edge. Qualification installs the exact packed tarball.

```ts
import { createTerraBaseClient } from "@terrascale/sdk";

const database = createTerraBaseClient({
  tenant: process.env.TERRABASE_TENANT!,
  database: process.env.TERRABASE_DATABASE!,
  shard: BigInt(process.env.TERRABASE_SHARD!),
  apiKey: process.env.TERRABASE_API_KEY!,
  endpoint: process.env.TERRABASE_ENDPOINT!,
});

const result = await database.read(collectionUuid, documentUuid);
if (!result.ok) throw new Error(result.error.code);
// Inspect absent, deleted, value or conflict. Preserve every conflict sibling.
console.log(result.value.state);
```

Configure the allocated direct `https://<region>.terrascale.tech` service origin.
The intended runtime route is authenticated Atlas selection followed by direct
regional requests. Its scoped UUID/permission/authority contract is not yet
admitted: requests without an explicit endpoint fail with
`discovery_contract_unavailable` before discovery, credentials or dispatch.
The historical database/branch discovery response cannot authorize native scope.
`api.terrascale.tech` is a test host and is rejected, including DNS trailing-dot
spellings. HTTP is allowed only on loopback for local development.

```ts
const transaction = await database.prepareCommit({
  transaction_id: {
    expires_at_unix_ms: BigInt(Date.now() + 10 * 60 * 1000),
    nonce: crypto.randomUUID().replaceAll("-", ""),
  },
  authority: { mode: "leaderless" },
  writes: [{
    collection: collectionUuid,
    document: documentUuid,
    schema_generation: schemaGeneration,
    schema_digest: schemaDigest,
    condition: { kind: "absent" },
    observed: [],
    value: {
      kind: "live",
      fields: [{ id: fieldId, value: { type: "utf8", value: "hello" } }],
    },
  }],
});

const committed = await database.commit(transaction);
if (!committed.ok && committed.error.mutation_outcome === "uncertain") {
  const status = await database.transactionStatus(transaction);
  // unknown, pending and expired do not establish that the write aborted.
}
```

Use the actual provisioned schema's field IDs, generation and digest. Field
arrays and writes must be strictly sorted. Omit a field to represent missing;
`{ type: "null" }` represents explicit null. Integer values are bigint;
byte values are Uint8Array. Maps use numeric field IDs and arrays preserve order.
For replace/delete, use the point's `state_version` condition and copy its full
`context` into `observed`. Observed causal context alone is not compare-and-swap.
All writes in a transaction share the client's declared shard.

Preparing snapshots the complete request and computes the producer's canonical
SHA-256 effect fingerprint. Reuse the prepared transaction on the same client
and provisioned authority endpoint for an exact retry.
A new effect needs a new transaction identity. There are no automatic mutation
retries. Retain the transaction identity, authority and fingerprint durably if
an application needs to resolve an interrupted request after restart.
`local_durable` acknowledges synced local storage; it does not claim replication
or global durability. `object_durable` is a distinct producer acknowledgement.

Security state requires the provisioned single-writer authority with its exact
generation and writer identity. Until scoped Atlas authority discovery is
admitted, configure that authority's explicit endpoint. Producer admission of
single-writer commits remains a release gate; leaderless writes are unsuitable
for authentication/security invariants.

The current query and subscription methods use bounded scalar predicates,
opaque cursors and complete typed points. Subscription snapshot continuation
and polling are separate phases. SQL is served by the producer's separate
PostgreSQL listener; the historical HTTP SQL route is not a current contract.
Public database/catalog/schema provisioning, credential lifecycle, backup and
restore require their owning service contracts before customer SDK admission.

The separate `@terrascale/sdk/ts-auth` entrypoint contains the hosted identity
service's management and OIDC client. Supply its actual public origin and
instance slug; a database key is never an identity management key.

## Qualification

```sh
npm ci
npm run typecheck
npm run build
npm exec vitest run src/database-codec.test.ts src/database.test.ts
npm run qualify:current-producer
```

The current producer journey installs a supplied tarball into a fresh temporary
npm project and imports only the installed package. It requires an artifact
path/SHA-256 and a real isolated producer allocation, never an in-process server.
Set `TERRASCALE_SDK_TARBALL`, `TERRASCALE_SDK_TARBALL_SHA256`,
`TERRABASE_TENANT`, `TERRABASE_DATABASE`, `TERRABASE_SHARD`,
`TERRABASE_COLLECTION`, `TERRABASE_SCHEMA_GENERATION`,
`TERRABASE_SCHEMA_DIGEST`, `TERRABASE_API_KEY`, `TERRABASE_DENY_API_KEY`,
`TERRABASE_TEST_FIELD_ID` and `TERRABASE_TEST_FIELD_TYPE` (`utf8` or `i64`).
`TERRABASE_ENDPOINT` supplies the allocated direct service origin while scoped
Atlas discovery remains unadmitted.
Missing configuration is a failing/incomplete gate. The journey covers exact
retry, changed-effect conflict, version update/delete, rejected/unknown/committed
status, query, subscription and scope denial. It emits redacted observations and
verifies document cleanup; retained tombstones require allocation-owner cleanup.

The staged package cutover still has historical public modules pending migration
or removal. Their presence is not producer compatibility or release evidence.
Current real-producer, deployed browser/account, full package cutover and schema/
management gates must pass before publishing a qualified integration release.
