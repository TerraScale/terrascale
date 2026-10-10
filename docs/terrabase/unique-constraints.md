# Collection uniqueness (TS-755)

Declare keys before writing records with `PUT /v1/projects/{project}/databases/{database}/schema/{collection}`
and `databases:write`. The collection must exist. This slice declares uniqueness, not document field types,
required fields, TTL, or the SDK's other schema features.

```json
{
  "fields": [
    { "name": "email", "unique": true, "normalization": "trim_lowercase" }
  ],
  "indexes": [
    { "name": "tenant_handle", "fields": ["tenant", "profile.handle"], "unique": true }
  ]
}
```

`fields[].unique` and `indexes[].unique` match ts-go's `Unique()` and `UniqueIndex()` declarations.
The response contains the effective `constraints`; `GET …/schema/{collection}` includes them as
`uniqueConstraints` alongside sampled field information. Repeating an unchanged declaration is idempotent.
Changing a declaration requires a collection with no retained document states, including tombstones;
otherwise the API answers `409 schema_requires_empty_collection`. Drop and recreate the collection to change it.
There is no upgrade, migration, or backfill of old files or consumer claim records.

## Values and limits

- A collection has at most 32 named keys; each holds 1 to 4 distinct dotted member paths. Paths follow the existing
  index path syntax. Names follow the existing collection name syntax. Field declarations receive stable derived names.
- A missing or JSON-null component excludes the document from that key. Strings, numbers and booleans are valid
  components; a complete key containing objects or arrays answers `400 invalid_request`. Sparse compound keys are
  omitted before validating any other components, regardless of declaration order. Types remain distinct: `1`, `"1"`, and `true`
  are different values. Numerically equal JSON spellings such as `1`, `1.0`, and `1e0` claim the same value, using
  the document contract's exact decimal representation.
- `none` preserves strings, `lowercase` uses .NET invariant lowercase, and `trim_lowercase` trims Unicode whitespace
  before invariant lowercase. The rule is deliberately explicit: it does not perform Unicode normalization or
  multi-character Unicode case folding. Normalization affects the key, not the stored document.
- Encoded keys fit 16 KiB each and 64 KiB per document. Local writes and derived visibility/history mutations share
  the existing transaction's write and history budgets. Scanning contenders does not consume those mutation budgets:
  a local delete or repair can resolve a conflict with more than 1024 contenders. Reconciliation pages 128 ids at a
  time and reads one body at a time. Replicated groups retain their input limits and stage
  derived history in disk-backed SQLite temporary tables. Collection size does not reject a valid incoming group.
  If derived history exceeds a bounded shape log chunk, the commit advances the history floor atomically;
  older shape cursors receive `409 must_refetch` and consumers restart from a current snapshot.

## Local transactions

The database's writer and SQLite transaction serialize key checks with document writes. A duplicate PUT or PATCH
answers `409 unique_violation`, with the message `a unique field or compound key is already taken`. A transaction
also reports the zero-based `error.op` of the refused operation. The whole request rolls back, including keys,
documents, journal and history changes; other requests in the writer's group commit keep their own savepoints.

Updates release the previous keys and reserve the new keys atomically. Deleting a document releases its keys.
Transaction operations run in their existing order, so release a key before assigning it to another document in
the same transaction. Uniqueness is scoped to one collection in one database.

## Replicated conflicts

Leaderless, partitioned replicas can both accept an otherwise valid write for the same unique value. This API does
not promise globally serialized admission or that every acknowledged write stays query-visible during a partition.

Every document first merges by the existing document merge rule. The engine retains each resulting source state
and indexes its unique keys. For each key, the document with the smallest UTF-8 document id wins. A document is
visible to lists, queries, SQL, counts and shapes only when it wins every key it claims. This is a deterministic
projection of merged source states, independent of replication arrival order. Schema declarations replicate as
reserved `_unique` documents with the same metadata merge rule as collections and indexes.

Point GET of a retained loser answers `409 unique_violation` and carries the source state's ETag. It can be changed
with PUT/PATCH or deleted, including with that ETag. Another document cannot acquire a locally contested value while
any retained state still claims it. Once a winner deletes or changes its key, the next contender becomes visible.
The engine records those visibility changes in shape history in the same transaction as the triggering write.
The collection change feed uses that history: hiding emits a deletion and promotion emits an upsert with the
retained JSON, at the triggering commit's offset. Identical repair emits no change-feed events.
Baseline replication includes losing source states, so a new replica computes the same result and does not lose
the information needed to release a key later.
Merkle digests and repair ranges also use retained source bodies, including hidden contenders; a visibility
projection is never a source tombstone. Identical state repair preserves conflicts and performs no mutation.

`unique_conflicts` stores the bodies excluded from the ordinary document view; `unique_keys` indexes every
contender. Neither is a consumer-visible collection or a `kind=unique_claim` document. Both are part of the fresh
on-disk schema. When a declaration and an incompatible state arrive from different origins, the receiver retains
that body as a conflict regardless of delivery order. Arrays, objects and over-limit keys claim no keys and are
excluded from queries; point GET returns 409 with the source ETag. A valid local replacement or removing the
constraint restores visibility. `unique_invalid` records these states separately from duplicate-key contenders.
Local writes under the declared schema still reject incompatible values with 400. Baselines carry source bodies,
so receivers recompute both kinds of conflict without losing data. Replicated collection drops end older retained
states and declarations even when the dropping origin had never received them.

## Rollout and follow-up slices

The fresh SQL schema (version 1) includes uniqueness storage and rejects files of any other schema version,
including snapshots, before import publication. It deploys with the single TerraBase storage reset that TS-677 runs
for the format change; verify and list the deleted targets in that PR. Bootstrap state recreates itself. No
migration command is shipped.

1. ts-go, ts-js and ts-dotnet: send single/compound uniqueness declarations and surface `unique_violation`.
2. ts-dashboard: declare its native keys, remove claim writers and the record discriminator, and wipe the affected
   data under the greenfield correction.
3. ts-auth: declare its native keys, remove `UniqueClaim` writers and the record discriminator, and wipe the affected
   data under the greenfield correction.

The greenfield correction supersedes the ticket's original suggestion of a one-time claim migration.
