# Collection uniqueness (TS-755)

Declare keys before writing records with `PUT /v1/projects/{project}/databases/{database}/schema/{collection}`
and `databases:write`. The collection must exist. This slice declares uniqueness, not document field types,
required fields, TTL, or the SDK's other schema features.

```json
{
  "fields": [
    { "name": "email", "unique": true, "normalization": "trim_case_fold" }
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
  components; objects and arrays answer `400 invalid_request`. Types remain distinct: `1`, `"1"`, and `true`
  are different values. Numerically equal JSON spellings such as `1`, `1.0`, and `1e0` claim the same value, using
  the document contract's exact decimal representation.
- `none` preserves strings, `case_fold` uses .NET invariant lowercase, and `trim_case_fold` trims Unicode whitespace
  before invariant lowercase. The rule is deliberately explicit: it does not perform Unicode normalization or
  multi-character Unicode case folding. Normalization affects the key, not the stored document.
- Encoded keys fit 16 KiB each and 64 KiB per document. A mutation reconciles at most 1024 affected documents and
  2 MiB of source bodies. Derived shape changes share the existing transaction's write and history budgets.

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
Baseline replication includes losing source states, so a new replica computes the same result and does not lose
the information needed to release a key later.

`unique_conflicts` stores the bodies excluded from the ordinary document view; `unique_keys` indexes every
contender. Neither is a consumer-visible collection or a `kind=unique_claim` document. Both are part of the fresh
on-disk schema. A replica that applies a changed declaration to states from another origin uses the same bounded
projection; exceeding its 1024-document/2-MiB bound fails the replication transaction without partial application.

## Rollout and follow-up slices

The fresh SQL schema includes uniqueness storage and rejects incompatible files. Coordinate its deployment and
exact storage reset with TS-754's layout-2 ULID cutover; verify and list the deleted targets in the PR. Bootstrap
state recreates itself. No migration command is shipped.

1. ts-go, ts-js and ts-dotnet: send single/compound uniqueness declarations and surface `unique_violation`.
2. ts-dashboard: declare its native keys, remove claim writers and the record discriminator, and wipe the affected
   data under the greenfield correction.
3. ts-auth: declare its native keys, remove `UniqueClaim` writers and the record discriminator, and wipe the affected
   data under the greenfield correction.

The greenfield correction supersedes the ticket's original suggestion of a one-time claim migration.
