---
name: firestore-to-terrascale
description: Migrate Firestore documents and Realtime Database JSON data to TerraScale with typed exports, relational modeling, batched SQL imports, query and security-rule replacement, reconciliation and rollback. Use when asked to migrate Firestore, move Firebase data to SQL, or replace Firestore collections and indexes with TerraScale tables.
---

# Migrate Firestore data to TerraScale

Use for the data portion of a Firebase migration. See also
`firebase-to-terrascale` for the service-wide rollout and
`firebase-auth-to-terrascale` for identity migration. No other skill is required
to use this one. Read the local [worked model](references/worked-model.md) when
designing the schema, transformation and verification queries.

## Prerequisites

- [ ] Firebase/Google Cloud project access, a complete database and collection
  inventory, and source IAM permissions for the chosen export route.
- [ ] Google Cloud CLI, Node.js 22 or later for the included exporter, and `jq`.
- [ ] Application Default Credentials from the existing credential workflow.
  Use `GOOGLE_APPLICATION_CREDENTIALS` only as a file path if needed; never embed
  a service-account key in source or print its contents.
- [ ] A private migration directory outside the repo, with `umask 077`, enough
  disk space and a separate TerraScale rehearsal database.
- [ ] Server-held TerraScale SQL read/write credentials and the selected project
  and database IDs; never put project keys in a browser or mobile app.
- [ ] A source write-freeze plan, or a complete mutation journal including deletes.

Sources: [Admin SDK setup](https://firebase.google.com/docs/admin/setup),
[Firestore export prerequisites](https://firebase.google.com/docs/firestore/manage-data/export-import),
[TerraScale getting started](https://docs.terrascale.tech/getting-started/).

## 1. Inventory data, queries and writers

Inspect `firebase.json`, `.firebaserc`, Firestore rules/index files and matching
client/Admin SDK calls. Use `rg -l` first to locate `getFirestore`,
`collectionGroup`, `collection`, `doc`, `where`, `orderBy`, `startAfter`,
`onSnapshot`, `runTransaction`, `writeBatch`, `FieldValue` and compat `.firestore(`
usage; inspect native SDK calls too. Record every database, including named
databases, and every service/tool that can mutate it.

Build one row per collection path pattern:

| Record | Example |
| --- | --- |
| Database / collection group / actual path | `(default)` / `tasks` / `workspaces/w1/tasks` |
| Parent and missing-parent behavior | Tasks may survive a deleted workspace document |
| ID and references | `tasks/t1`, assignee `users/u1`, possibly cross-database |
| Field type variants | Missing/null `dueAt`, maps, GeoPoint, timestamps, byte data |
| Bounds | Document count, bytes, largest array/map and largest document |
| Read path | Filter, sort, projection, limit, cursor, listener and offline use |
| Write path | Create/update/delete, transforms, transaction and triggers |
| Authorization | Owner/team access, field validation, roles, cross-document checks |

Collection groups include every collection/subcollection with the same final ID.
They are not one parent collection, and exporting one group does not recursively
include differently named child groups. Deleting a parent document does not
delete its subcollections. Sources:
[export collection groups](https://firebase.google.com/docs/firestore/manage-data/export-import),
[data model](https://firebase.google.com/docs/firestore/data-model),
[delete data](https://firebase.google.com/docs/firestore/manage-data/delete-data).

- [ ] Confirm group names against the console, source code and rules, rather
  than deriving them only from existing parent documents.
- [ ] Record counts per actual collection path and per group, and arrays' element
  totals. A group total alone can hide a missing tenant or path collision.
- [ ] Capture composite indexes, exemptions, TTL behavior and extension writers.
- [ ] Identify unsupported/special values before using the exporter; do not
  discover a precision-loss problem after deleting the source.
- [ ] Confirm every reference stays in this source project/database before
  using the Admin exporter; otherwise use the typed REST route described below.

## 2. Plan relational representations

TerraScale uses the SQLite dialect, supports enforced foreign keys and stores
JSON as text with `json_extract`/`json_each`. Values are null, 64-bit integer,
double, text or blob. Source: [SQL](https://docs.terrascale.tech/concepts/sql).

| Firestore concept | Explicit SQL representation |
| --- | --- |
| Collection | Table per logical entity; merge same-named groups only when their schemas match |
| Document ID | Preserve as text; use full document path or a collision-free composite key |
| Subcollection | Child table with full source path plus parent ID/FK when the parent exists |
| Nested map | Typed columns for queried fields; JSON text for the remaining map |
| Array | Child rows with an ordinal, or tagged JSON text if never queried relationally |
| DocumentReference | Full path plus source project/database; validate before resolving to a target FK |
| Timestamp | Exact seconds and nanoseconds columns, or a chosen fixed UTC representation with documented precision |
| GeoPoint | Latitude and longitude numeric columns; geospatial algorithms remain app logic unless separately documented |
| Boolean | Integer 0/1 with a check constraint |
| Integer/double | Exact integer or finite real with explicit range/precision policy |
| Bytes | BLOB; HTTP parameter `{base64: ...}` |
| Missing versus null | Presence flag or retained tagged map when the distinction matters |

Preserve array order and duplicates. Distinguish an absent field, explicit null,
empty string, empty map and empty array. Do not turn a Firestore reference into
an FK until its referenced project/database/path is known and resolvable.
Firestore timestamps are stored to microsecond precision, but preserving the
SDK's seconds/nanoseconds components avoids conversion through JS milliseconds.
Source: [Firestore data types](https://firebase.google.com/docs/firestore/manage-data/data-types).

- [ ] Choose a deterministic key policy and retain source IDs for reverse mapping.
- [ ] Resolve missing parents and dangling references: repair, quarantine, or
  deliberately represent them without an FK; never invent a business parent.
- [ ] Choose JSON and numeric codecs, nullable fields and exact timestamp ordering.
- [ ] Preserve a typed raw archive until validation and rollback retention end.
- [ ] Define deletes and update/merge semantics, not only initial inserts.
- [ ] Define UID-to-canonical-ULID mapping for scoped end-user tokens separately
  from the application's preserved document IDs.

See [worked model](references/worked-model.md) for complete source documents,
DDL, field conversion rules, bound import examples, queries and count checks.

## 3. Export a recoverable copy and typed interchange data

### Managed backup

Use an existing authorized bucket and selected project/database:

```sh
gcloud firestore export "gs://$FIRESTORE_EXPORT_BUCKET/$EXPORT_PREFIX" \
  --project "$FIREBASE_PROJECT_ID" --database "$FIRESTORE_DATABASE_ID"
```

For a deliberately partial backup, add
`--collection-ids=users,workspaces,tasks`, replacing that allowlist with the
complete audited groups. The service account needs the documented bucket and
Firestore permissions. The operation may incur billed reads. Wait for successful
completion before treating it as recovery evidence.
Source: [Firestore managed export](https://firebase.google.com/docs/firestore/manage-data/export-import).

Managed export files are not SQL or a JSONL dump that TerraScale can import.
Keep them for Firebase recovery. An export is not an exact snapshot at start;
it can include concurrent changes. For consistent final data, stop every writer
and let in-flight changes settle before either export route below.

### Streaming Admin SDK JSONL

The included [exporter](scripts/export-firestore.mjs) queries an explicit list of
collection **groups**, including descendants whose parent document is missing.
It streams each document to JSONL with backpressure, a full source path and a
typed field envelope. It does not discover group IDs automatically and does not
make a whole-database snapshot. Source API contracts:
[Admin setup](https://firebase.google.com/docs/admin/setup),
[Firestore collectionGroup](https://cloud.google.com/nodejs/docs/reference/firestore/latest/firestore/firestore#collectiongroupcollectionid),
[Query stream](https://cloud.google.com/nodejs/docs/reference/firestore/latest/firestore/query#stream).

Set `SKILL_DIR` to this installed skill's directory and `MIGRATION_DIR` to the
private working directory. Copy the script there so its SDK dependency resolves
from the same directory; keep the resulting dependency files out of the app repo:

```sh
umask 077
mkdir -p "$MIGRATION_DIR"
cp "$SKILL_DIR/scripts/export-firestore.mjs" "$MIGRATION_DIR/export-firestore.mjs"
cd "$MIGRATION_DIR"
npm init -y
npm install firebase-admin
node export-firestore.mjs --dry-run "$FIREBASE_PROJECT_ID" \
  "$FIRESTORE_DATABASE_ID" users workspaces tasks
node export-firestore.mjs "$FIREBASE_PROJECT_ID" "$FIRESTORE_DATABASE_ID" \
  users workspaces tasks > documents.partial.jsonl 2> export-status.txt
```

Check the exit status immediately. Rename the partial file to
`documents.jsonl` only after success; never process a partial export. Keep the
SDK lockfile/version and source freeze checkpoint in the migration manifest.
`--dry-run` validates arguments and prints the group count; it does not load
credentials or the SDK, contact Firebase or prove IAM permissions.

Exporter limits and contract:

- Each line is `{path, collection, parent, id, fields}`. `fields` is a typed map;
  see the worked reference for its codec and transformation procedure.
- Duplicate group arguments and group names containing `/` are rejected.
  Results are not a globally ordered stream; transform by entity dependency.
- Nested maps/arrays, null, strings, booleans, timestamps, references, byte arrays
  and JS numbers have explicit tags. Unknown SDK value objects fail the export.
- The reference qualifier is the SDK-decoded project/database. The Node client
  decodes references through their relative path, so cross-project/database
  reference identity is not safely preserved by this route. Use a typed REST
  reader retaining the complete `referenceValue` resource name when such
  references exist; do not rely on the exported qualifier to discover them.
- Integers outside JS's safe range fail rather than silently becoming SQL data.
  The Admin SDK's decoded numbers do not retain the integer-versus-double field
  distinction. For exact 64-bit source integers or exact source type fidelity,
  use a typed Firestore REST export preserving `integerValue` strings and
  `doubleValue`, or a reviewed decoder for the managed backup. Do not convert
  already-rounded JS numbers back into integers.
- `NaN`, infinities and negative zero are encoded as strings in number tags;
  decide a target representation before importing them. Vector/Enterprise-only
  types require a reviewed additional codec; the script does not claim support.
- Only completion counts go to stderr; document content goes to the private
  output file. Treat that file as sensitive user data.

The typed REST value contract is documented at
[Firestore Value](https://cloud.google.com/firestore/docs/reference/rest/v1/Value).

## 4. Transform and load in bounded batches

1. Validate every JSONL line against the chosen path/field/type map. Reject
   unexpected variants into a private exception report, with counts in the
   migration manifest; do not silently drop fields or tenants.
2. Retain the typed raw document once, then create deterministic normalized
   rows. Use multiple passes or a local spool to load users, parents, children,
   then array rows. The JSONL group sequence is not a relationship guarantee.
3. Create target tables first. For the example DDL and transformation, use
   [worked model](references/worked-model.md); adapt every field before running.
4. Emit parameterized INSERT statements and a checkpoint manifest. Preserve
   insertion dependencies across transactions; foreign keys are enforced.
5. Start below the documented upper bounds: at most 256 statements, each at most
   16 KiB, body at most 1 MiB, 1,024 rows changed and 8 MiB file growth per
   transaction. Count triggers/cascades and array expansion in the row budget.
   Split early for large values and plan-dependent query budgets.
6. POST batches to the documented SQL endpoint or use the SDK/CLI import route.
   Record the input range, hash and committed result after each success.
7. Stop at a refused batch, which rolls back that request. Earlier requests stay
   committed. For an ambiguous response, inspect deterministic keys/checkpoints
   before resending. Rebuild the rehearsal database when the mapping changes.

Sources: [import](https://docs.terrascale.tech/guides/import),
[SQL over HTTP](https://docs.terrascale.tech/guides/sql-http),
[limits](https://docs.terrascale.tech/limits).
An individual source document can exceed the destination HTTP body budget after
tagging/encoding. Archive it outside SQL or split its business representation;
do not truncate it to make an import pass.

If generating SQL rather than parameter JSON, use a reviewed SQL serializer,
then `ts db import "$TERRASCALE_DATABASE_NAME" converted.sql`. The CLI batches
the script; `-from <n>` resumes at its reported 1-based statement number. Do not
feed managed Firestore files or JSONL to that command. Source:
[CLI import](https://docs.terrascale.tech/guides/import#with-the-cli).

## 5. Replace queries, indexes and security rules

Translate filters, sorts and cursors together. Firestore `orderBy` excludes
documents missing the ordered field; SQL NULL behavior needs an explicit
presence/null policy. Use stable tie-breakers based on preserved path/ID, not
an offset cursor. Model `array-contains` as a child-table predicate or JSON query;
avoid changing duplicate or null semantics accidentally. Sources:
[Firestore query ordering](https://firebase.google.com/docs/firestore/query-data/order-limit-data),
[SQL](https://docs.terrascale.tech/concepts/sql).

Replace Firestore composite indexes with SQL indexes tailored to the translated
query: equality columns first, then range/order and stable tie-breaker columns.
Use `EXPLAIN QUERY PLAN` on the destination and check its detail field names the
index. Plain, unique, partial and expression indexes are documented; index DDL
is a write and obeys transaction limits. Do not import `firestore.indexes.json`
as SQL. Source: [indexes](https://docs.terrascale.tech/guides/indexes).

Firestore rules are not SQL grants or TerraScale rule syntax. Admin SDK reads
bypass Firestore rules and are governed by IAM, so a successful export says
nothing about end-user authorization:
[Firestore rule conditions](https://firebase.google.com/docs/firestore/security/rules-conditions).

- [ ] Translate each read/write rule into an explicit target enforcement point.
- [ ] Map `request.auth.uid` through the trusted identity map; derive roles on
  the backend, never from client-supplied role/owner fields.
- [ ] Use rule-gated row/shape reads with backend-minted scoped tokens where the
  documented rule model covers the policy; otherwise authorize through backend SQL.
- [ ] Enforce write validation, ownership, cross-document invariants and allowed
  fields in the backend and appropriate SQL constraints/atomic batches.
- [ ] Deny direct client operations whose policy has not been implemented.
- [ ] Rehearse another user's IDs, changed owner fields, role escalation and
  unauthenticated/expired sessions; verify actual denied responses.

Scoped tokens require canonical ULID `user_id`; their asserted table names are
ignored by TerraBase. A SQL/shape filter does not replace read rules. Follow the
Rules link from the public [end-user guide](https://docs.terrascale.tech/guides/end-user-authentication#data-access-for-your-users)
for the current rule contract rather than inventing syntax here. The newer
[authentication capabilities](https://docs.terrascale.tech/guides/authentication-capabilities)
page says project OIDC tokens do not authorize data APIs; use the backend mint
flow. No public guide establishes automatic Firebase rule translation.

## 6. Verify, cut over and roll back

- [ ] Compare frozen source counts, JSONL counts and target archive counts per
  actual collection path. Compare normalized rows and array-element totals too.
- [ ] Compare canonical field hashes keyed by full path, with a codec preserving
  missing/null, exact timestamps, byte hashes and reference identity.
- [ ] Run FK/orphan checks, duplicate-key checks and representative query/cursor
  comparisons. Counts alone do not prove correct values.
- [ ] Verify all translated authorization rules and stale-client rejection.
- [ ] Rehearse an interrupted import and its checkpoint recovery.
- [ ] Record all exceptions; cut over only with the agreed reconciled result.

Freeze all source writers, drain events, run the final export/delta and repeat
verification. Switch the authorized backend and client cohort, exercise a full
mutation flow, then open target writes. Stop old clients, scheduled writers and
Firebase triggers from continuing to mutate the source independently.

Before target writes, rollback is a routing change followed by source unfreeze.
After target writes, freeze both sides and reverse-map target mutations and
deletes before reopening Firebase. Preserve source paths, identity maps, files
and tombstones for that replay. A TerraScale SQL export is a consistent read
snapshot but still needs reverse conversion:
[SQL export](https://docs.terrascale.tech/guides/sql-http#export-a-database).

## 7. Realtime Database JSON trees

Export a frozen RTDB instance with the real CLI command:

```sh
firebase database:get / --instance "$FIREBASE_RTDB_INSTANCE" \
  --project "$FIREBASE_PROJECT_ID" > rtdb.json
```

Source: [Firebase CLI database commands](https://firebase.google.com/docs/cli#database-commands).
For large trees, partition at audited stable paths and verify counts per path;
separate reads are not a global snapshot. Record priorities separately if the
app uses them; do not assume ordinary JSON captures every RTDB behavior.
Normalize objects keyed by push ID into tables preserving those text IDs, parent
paths and array positions. Flatten denormalized fan-out copies only after
identifying their authoritative source. Migrate RTDB transactions and rule
validation into backend/SQL operations, then compare all fan-out projections.
RTDB presence and `onDisconnect` have no documented compatible contract in the
cited TerraScale SQL guide: retain RTDB or implement and verify app-owned
heartbeat/expiry presence. Source behavior:
[RTDB offline capabilities](https://firebase.google.com/docs/database/web/offline-capabilities),
target model: [TerraScale SQL](https://docs.terrascale.tech/concepts/sql).
