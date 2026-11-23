# Worked Firestore model and import

This example is a small team task app. Adapt the field contract to the actual
inventory; do not run the example INSERTs against production data.

## Source schema and IDs

```text
users/u_ada
  displayName: "Ada"
  email: "ada@example.test"
workspaces/w_launch
  name: "November launch"
  owner: DocumentReference(users/u_ada)
  createdAt: Timestamp(seconds=1794873600, nanoseconds=123456000)
  settings: { theme: "dark", digest: true }
workspaces/w_launch/tasks/t_7
  title: "Prepare launch checklist"
  assignee: DocumentReference(users/u_ada)
  done: false
  dueAt: null
  labels: ["release", "customer", "release"]
  location: GeoPoint(latitude=-23.5505, longitude=-46.6333)
```

`t_7` is unique only within its collection. Use the full document path as the
task key, preserving its short ID too. Map the workspace parent path to an FK;
do not infer ownership from a path the caller supplies. Source concepts:
[Firestore data model](https://firebase.google.com/docs/firestore/data-model).

## Destination DDL

Create the tables in this order, with SQL sent through the
[import API](https://docs.terrascale.tech/guides/import). JSON is TEXT and foreign
keys are enforced: [SQL](https://docs.terrascale.tech/concepts/sql).

```sql
CREATE TABLE source_documents (
  path TEXT PRIMARY KEY,
  collection_path TEXT NOT NULL,
  encoded TEXT NOT NULL
);
CREATE TABLE app_users (
  id TEXT PRIMARY KEY,
  firebase_uid TEXT NOT NULL UNIQUE,
  display_name TEXT NOT NULL,
  email TEXT NOT NULL
);
CREATE TABLE identity_map (
  firebase_uid TEXT PRIMARY KEY REFERENCES app_users(firebase_uid),
  subject TEXT NOT NULL UNIQUE
);
CREATE TABLE workspaces (
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  owner_id TEXT NOT NULL REFERENCES app_users(id),
  created_seconds INTEGER NOT NULL,
  created_nanos INTEGER NOT NULL CHECK (created_nanos >= 0 AND created_nanos < 1000000000),
  settings_json TEXT NOT NULL
);
CREATE TABLE tasks (
  id TEXT PRIMARY KEY,
  source_id TEXT NOT NULL,
  workspace_id TEXT NOT NULL REFERENCES workspaces(id),
  title TEXT NOT NULL,
  assignee_id TEXT REFERENCES app_users(id),
  done INTEGER NOT NULL CHECK (done IN (0, 1)),
  due_present INTEGER NOT NULL CHECK (due_present IN (0, 1)),
  due_seconds INTEGER,
  due_nanos INTEGER CHECK (due_nanos >= 0 AND due_nanos < 1000000000),
  latitude REAL,
  longitude REAL,
  CHECK ((due_seconds IS NULL) = (due_nanos IS NULL)),
  CHECK (due_present = 1 OR due_seconds IS NULL),
  CHECK ((latitude IS NULL) = (longitude IS NULL))
);
CREATE TABLE task_labels (
  task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
  position INTEGER NOT NULL CHECK (position >= 0),
  label TEXT NOT NULL,
  PRIMARY KEY (task_id, position)
);
```

`identity_map` is populated by the trusted identity workflow, not by accepting
client-supplied subjects. Validate canonical ULID subjects in that workflow.
The scoped token's subject can differ from a ts-auth account, but the backend
owns the mapping: [end-user tokens](https://docs.terrascale.tech/guides/end-user-authentication#data-access-for-your-users).
Keep this map out of ordinary users' readable data.

## Export codec and transformation contract

The exporter wraps each value, including maps, so a source map containing a
literal `type` field cannot collide with a special-value marker. Examples:

```json
{"type":"timestamp","seconds":"1794873600","nanoseconds":123456000}
{"type":"reference","project":"source-project","database":"(default)","path":"users/u_ada"}
{"type":"array","value":[{"type":"string","value":"release"},{"type":"string","value":"customer"},{"type":"string","value":"release"}]}
{"type":"map","value":{"theme":{"type":"string","value":"dark"},"digest":{"type":"boolean","value":true}}}
```

Every document's `fields` has the final map shape above. A missing field has no
map entry; null is `{type:"null", value:null}`. A number is a string-valued
`number` tag, bytes have `type:"bytes", base64`, and GeoPoint has
`type:"geopoint", latitude, longitude`. Preserve the entire exported record as
JSON in `source_documents.encoded` before normalizing it.

Apply this transformation, recording a rejection for every violated contract:

| Target | Exact conversion |
| --- | --- |
| Archive | `path`, `collection`, JSON serialization of the complete record |
| User | Only root `users/<id>` paths; `id = path`, `firebase_uid = id`; required string `displayName`, `email` |
| Workspace | Only root `workspaces/<id>`; name string; same-project/database owner reference resolving to an app user |
| Creation time | Required timestamp; convert decimal `seconds` exactly to a signed 64-bit integer and retain `nanoseconds` |
| Settings | Decode ordinary nested tags into JSON; reject special types unless the chosen settings schema supports them |
| Task | Exactly `workspaces/<workspace>/tasks/<id>`; `id = path`, `source_id = record.id`, `workspace_id = record.parent` |
| Assignee | Missing/null -> SQL NULL; otherwise same-source reference resolving to an app user |
| Done/title | Required boolean -> 0/1; required string title |
| Due | Missing -> `(0, NULL, NULL)`; explicit null -> `(1, NULL, NULL)`; timestamp -> `(1, seconds, nanoseconds)` |
| Location | Missing/null -> two NULLs; GeoPoint -> latitude/longitude, checking valid coordinate ranges |
| Labels | Required string array -> one row per element with zero-based position; keep duplicate labels |

The archive preserves distinctions not projected into a business column. If
those distinctions affect an app query, add a typed/presence column before
cutover. A dangling owner, task parent or assignee is a migration exception;
resolve it using the recorded source policy before loading the dependent rows.
Do not regenerate IDs or turn an unmatched reference into an empty string.
This example assumes all references stay in the selected source database. For
cross-database/project references, use a REST-value export retaining the full
resource name before applying this map; decoded Admin references are insufficient.

Reject `NaN`/infinity for ordinary numeric columns; use an explicit tagged
representation if business data needs them. Do not parse 64-bit integers through
JS `Number`. The exporter intentionally refuses unsafe integer values; a typed
REST reader must preserve `integerValue` strings before SQL binding. Sources:
[Firestore types](https://firebase.google.com/docs/firestore/manage-data/data-types),
[REST Value](https://cloud.google.com/firestore/docs/reference/rest/v1/Value),
[TerraScale SQL values](https://docs.terrascale.tech/concepts/sql).

## Parameterized load and checkpoints

For the sample, create a private `batch.json` with bound arguments:

```json
{"statements":[
  {"sql":"INSERT INTO app_users (id, firebase_uid, display_name, email) VALUES (?, ?, ?, ?)","args":["users/u_ada","u_ada","Ada","ada@example.test"]},
  {"sql":"INSERT INTO workspaces (id, name, owner_id, created_seconds, created_nanos, settings_json) VALUES (?, ?, ?, ?, ?, ?)","args":["workspaces/w_launch","November launch","users/u_ada",1794873600,123456000,"{\"theme\":\"dark\",\"digest\":true}"]},
  {"sql":"INSERT INTO tasks (id, source_id, workspace_id, title, assignee_id, done, due_present, due_seconds, due_nanos, latitude, longitude) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)","args":["workspaces/w_launch/tasks/t_7","t_7","workspaces/w_launch","Prepare launch checklist","users/u_ada",0,1,null,null,-23.5505,-46.6333]},
  {"sql":"INSERT INTO task_labels (task_id, position, label) VALUES (?, ?, ?)","args":["workspaces/w_launch/tasks/t_7",0,"release"]},
  {"sql":"INSERT INTO task_labels (task_id, position, label) VALUES (?, ?, ?)","args":["workspaces/w_launch/tasks/t_7",1,"customer"]},
  {"sql":"INSERT INTO task_labels (task_id, position, label) VALUES (?, ?, ?)","args":["workspaces/w_launch/tasks/t_7",2,"release"]}
]}
```

Archive rows are loaded separately, one for each complete exported record. The
sample omits them only to keep the business-row batch readable. Keep archive
and normalized reconciliation in the same migration manifest.

Use a current exchanged access token; a raw project API key is not a SQL bearer
token. Its short lifetime requires the documented exchange/provider refresh
workflow for longer imports: [SQL over HTTP](https://docs.terrascale.tech/guides/sql-http).

```bash
curl -sS --fail-with-body --header @/dev/fd/3 \
  -H 'Content-Type: application/json' --data-binary @batch.json \
  "https://api.terrascale.tech/v1/projects/$TERRASCALE_PROJECT_ID/databases/$TERRASCALE_DATABASE_NAME/sql/execute" \
  3< <(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN")
```

Compute UTF-8 byte length of the serialized request, not the source JSONL size.
Split before any statement/body/row-growth bound in the
[import guide](https://docs.terrascale.tech/guides/import). A large label array
may need several batches; keep readers disabled until reconciliation completes.
Record `(input hash, phase, first key, last key, batch hash, committed)` privately.
Do not use `INSERT OR IGNORE` to conceal mismatches when resuming: compare the
existing row against the intended row, then resume or rebuild deliberately.

## Query and composite-index translation

For a Firestore tasks query filtered by assignee and completion, ordered by
due time, use an equivalent nullable/presence policy and stable path tie-breaker:

```sql
CREATE INDEX tasks_by_assignee_due
ON tasks (workspace_id, assignee_id, done, due_present, due_seconds, due_nanos, id);
SELECT id, title, due_seconds, due_nanos
FROM tasks
WHERE workspace_id = ? AND assignee_id = ? AND done = 0 AND due_present = 1
ORDER BY due_seconds, due_nanos, id
LIMIT 50;
CREATE INDEX labels_by_value ON task_labels (label, task_id);
SELECT t.id, t.title FROM tasks t
WHERE t.workspace_id = ? AND EXISTS (
  SELECT 1 FROM task_labels l WHERE l.task_id = t.id AND l.label = ?
)
ORDER BY t.id LIMIT 50;
```

`EXISTS` avoids duplicating the task when the array contains a repeated label.
For timestamps that are non-null, a keyset cursor uses
`(due_seconds, due_nanos, id) > (?, ?, ?)` with the same ORDER BY. Handle the
explicit-null segment separately; ordinary comparisons with NULL do not match.
Compare these decisions with the source query's null/missing behavior before
shipping. Use bound values with `EXPLAIN QUERY PLAN` to confirm index selection:
[indexes](https://docs.terrascale.tech/guides/indexes),
[Firestore order/limit](https://firebase.google.com/docs/firestore/query-data/order-limit-data).

## Reconciliation and access checks

Count JSONL documents per actual collection path into a private file. This
streams extraction and lets `sort` spill to disk rather than retaining every
document in memory:

```sh
jq -r '.collection' documents.jsonl | LC_ALL=C sort | uniq -c > source-collection-counts.txt
```

Compare with frozen source counts and these destination queries:

```sql
SELECT collection_path, count(*) AS n FROM source_documents GROUP BY collection_path;
SELECT 'users' AS collection_path, count(*) AS n FROM app_users;
SELECT 'workspaces' AS collection_path, count(*) AS n FROM workspaces;
SELECT workspace_id || '/tasks' AS collection_path, count(*) AS n
FROM tasks GROUP BY workspace_id;
SELECT task_id, count(*) AS labels FROM task_labels GROUP BY task_id;
PRAGMA foreign_key_check;
SELECT t.id FROM tasks t LEFT JOIN workspaces w ON w.id = t.workspace_id WHERE w.id IS NULL;
```

For the sample, expect three archive records, one user, one workspace, one task,
three label rows and zero FK/orphan results. Also compare canonical typed values
for every path, not just totals. Counts from the exporter cannot independently
prove that the collection-group allowlist covered the database.

Keep project SQL credentials on the backend. It resolves the validated caller's
mapped user ID and checks workspace ownership/membership before every task
read or mutation, including supplied task/workspace/assignee IDs. A mutation
can use a conditional statement or atomic batch so that the access check and
write cannot race. Restrict updated fields and recheck references; do not accept
a caller's claimed owner. For direct reads, implement the documented TerraScale
read rules and scoped token flow instead. Sources:
[SQL transactions](https://docs.terrascale.tech/concepts/sql),
[end-user access](https://docs.terrascale.tech/guides/end-user-authentication#data-access-for-your-users).
Verify a second user cannot read/change the example workspace or bypass that
check with a known task ID. Exercise successful owner access and all denials
before opening traffic; a privileged import is not an authorization rehearsal.
