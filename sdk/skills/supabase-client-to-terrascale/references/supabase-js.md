# Supabase JavaScript/TypeScript to TerraScale

Read this for Next.js, Node.js and browser call-site rewrites. All destination SQL
below runs on the server. Examples assume migrated projects(id, owner_id, name)
and tasks(id, project_id, title, done, created_at), with TEXT IDs, integer done,
foreign keys and the original unique constraints. ownerId comes from a validated
server session and destination identity mapping, never the request body.
Variables such as id, title, projectId and pageStart are validated app inputs.

Sources: [Supabase JS reference](https://supabase.com/docs/reference/javascript),
[select/relationships](https://supabase.com/docs/reference/javascript/select),
[filters](https://supabase.com/docs/reference/javascript/using-filters),
[RPC](https://supabase.com/docs/reference/javascript/rpc).
Destination calls follow the [JS quickstart](https://docs.terrascale.tech/getting-started/javascript),
[SQL HTTP](https://docs.terrascale.tech/guides/sql-http) and
[SQL model](https://docs.terrascale.tech/concepts/sql).

API audit: TerraScale origin/main at da3382de73201d4fc94dd6872fd2cad5a97de12f,
including sdk/js/src/index.js, v1/terrabase-v1.js, v1/sql-execute.js,
v1/table-shape.js and v1/shape-subscriptions.js. Recheck the quickstart and exports
when updating the app's pinned SDK revision.

## Construction and a server module

Before:

~~~ts
import { createClient } from "@supabase/supabase-js";

const supabase = createClient(
  process.env.SUPABASE_URL!,
  process.env.SUPABASE_KEY!,
);
~~~

After: install from a reviewed source checkout as the quickstart requires; do not
assume the previous npm version has these methods.

~~~sh
npm install /path/to/terrascale/sdk/js
~~~

Put the following in a server data-access module. In Next.js, use the app's
server-only module guard and call this module from authenticated routes/actions.
Read required variables without printing their values.

~~~ts
import { createTerraBaseV1Client, SqlError } from "@terrascale/sdk";

function required(name: string): string {
  const value = process.env[name];
  if (!value) throw new Error("Missing " + name);
  return value;
}

const client = createTerraBaseV1Client({
  baseUrl: "https://api.terrascale.tech",
  project: required("TERRASCALE_PROJECT_ID"),
  apiKey: required("TERRASCALE_API_KEY"),
});
const db = client.database(required("TERRASCALE_DATABASE_NAME"));
~~~

The client exchanges its API key automatically. query returns object rows;
execute returns columns, array rows, rowsAffected and lastInsertRowid; batch
returns one execute-style result per statement. These are not PostgREST envelopes
([quickstart](https://docs.terrascale.tech/getting-started/javascript)).

## Full operation map

Use the map to classify every builder chain, then adapt the examples below.
Parameters bind values only: tables, columns, sort directions and projections
must come from fixed code or a strict allowlist.

| Supabase chain | SQL/application replacement |
| --- | --- |
| select("id,title") | SELECT id, title; alias renamed columns explicitly. |
| eq / neq / gt / gte / lt / lte | = / <> / > / >= / < / <= with bound parameters. |
| match({a,b}) | a = ? AND b = ?; handle null separately. |
| is("column", null) / not(...,"is",null) | IS NULL / IS NOT NULL, not = NULL. |
| in("id", ids) | IN (?, ...), one placeholder per item; empty input returns empty rows. |
| like / ilike | LIKE with a bound pattern; verify case behavior, especially Unicode. |
| or("a.eq.x,b.eq.y") | Parenthesized (a = ? OR b = ?) under the authorization predicate. |
| not / filter | Parse the actual operator into reviewed SQL; never splice PostgREST syntax into SQL. |
| select("*,projects(name)") | LEFT JOIN and DTO assembly; !inner uses INNER JOIN. |
| insert(values).select() | INSERT ... RETURNING explicit columns via query. |
| upsert(values,{onConflict:"id"}) | INSERT ... ON CONFLICT(id) DO UPDATE SET ... RETURNING ... . |
| upsert(...,{ignoreDuplicates:true}) | ON CONFLICT(the_actual_unique_key) DO NOTHING; preserve no-row result. |
| update(values).eq(...) | UPDATE ... SET ... WHERE ...; retain all filters and ownership checks. |
| delete().eq(...) | DELETE ... WHERE ...; retain all filters and ownership checks. |
| order / limit / range | ORDER BY ... LIMIT ? OFFSET ?; inclusive range length is end-start+1. |
| count:"exact", head:true | SELECT COUNT(*) AS n with identical filters, no row projection. |
| single / maybeSingle | Check length explicitly, distinguishing zero, one and multiple rows. |
| rpc(name,args) | Translate its definition to SQL/batch or backend logic; no assumed stored RPC. |
| csv / explain / abortSignal | App serialization / separately reviewed query diagnostics / SDK request cancellation; no envelope compatibility. |

JSON can use json_extract/->/->> and json_each; JSON is TEXT in the documented
[SQL model](https://docs.terrascale.tech/concepts/sql). PostgreSQL arrays, range
operators, full-text search, extensions and custom function calls have no automatic
builder translation. Normalize collections or design a reviewed SQL/application
replacement; record semantic differences instead of substituting approximate matches.

## Select, filter, order, page and cardinality

Before: Supabase RLS limits projects to this user.

~~~ts
const { data, error } = await supabase.from("projects")
  .select("id,name").eq("owner_id", ownerId)
  .ilike("name", pattern).order("name").order("id").range(pageStart, pageEnd);
if (error) throw error;
~~~

After: verify the authorization predicate against the old policy. LIKE uses the
destination dialect; do not promise identical Unicode matching.

~~~ts
const data = await db.query(
  "SELECT id, name FROM projects WHERE owner_id = ? AND name LIKE ? ORDER BY name, id LIMIT ? OFFSET ?",
  [ownerId, pattern, pageEnd - pageStart + 1, pageStart],
);
~~~

Validate nonnegative integer offsets and a bounded positive page length before the
call. For nullable sorting, emit an explicit null order matching the old chain.
A unique tie-breaker makes tied sort values stable, but offset pages can still
shift under concurrent writes; use an agreed keyset cursor when required.

For single()/maybeSingle(), query with the original filters and LIMIT 2, then:

~~~ts
function cardinality<T>(rows: T[], optional: boolean): T | null {
  if (rows.length > 1) throw new Error("Expected at most one row");
  if (rows.length === 0) {
    if (optional) return null;
    throw new Error("Expected exactly one row");
  }
  return rows[0];
}
~~~

Do not implement single() by silently picking rows[0]. Keep the old app's domain
error mapping; these local errors are not Supabase error codes.

## Nested relationships: one-to-many and missing parents

Before:

~~~ts
const result = await supabase.from("projects")
  .select("id,name,tasks(id,title)").eq("owner_id", ownerId).order("id");
if (result.error) throw result.error;
~~~

After: alias duplicate column names and group children. For a many-to-one embedded
object, assign an object or null instead of an array. Preserve relationship aliases.

~~~ts
type Project = { id: string; name: string; tasks: { id: string; title: string }[] };
const flat = await db.query(
  "SELECT p.id, p.name, t.id AS task_id, t.title AS task_title FROM projects p LEFT JOIN tasks t ON t.project_id = p.id WHERE p.owner_id = ? ORDER BY p.id, t.id",
  [ownerId],
);
const grouped = new Map<string, Project>();
for (const row of flat) {
  const id = String(row.id);
  let project = grouped.get(id);
  if (!project) {
    project = { id, name: String(row.name), tasks: [] };
    grouped.set(id, project);
  }
  if (row.task_id !== null) {
    project.tasks.push({ id: String(row.task_id), title: String(row.task_title) });
  }
}
const data = [...grouped.values()];
~~~

Check child policies too: if tasks have a stricter policy than project ownership,
add that predicate to the JOIN condition. A WHERE predicate on a nullable child
can accidentally remove parents. Paginate parents before joining children;
LIMIT on the flattened join truncates children and changes parent page sizes.

## Insert, upsert, update and delete

Before (four independent examples):

~~~ts
await supabase.from("projects").insert({ id, owner_id: ownerId, name }).select("id,name");
await supabase.from("projects").upsert({ id, owner_id: ownerId, name }, { onConflict: "id" }).select("id,name");
await supabase.from("projects").update({ name }).eq("id", id).select("id,name");
await supabase.from("projects").delete().eq("id", id).select("id,name");
~~~

After: retain owner predicates even if RLS formerly supplied them implicitly.

~~~ts
const inserted = await db.query(
  "INSERT INTO projects (id, owner_id, name) VALUES (?, ?, ?) RETURNING id, name",
  [id, ownerId, name],
);
const upserted = await db.query(
  "INSERT INTO projects (id, owner_id, name) VALUES (?, ?, ?) ON CONFLICT(id) DO UPDATE SET name = excluded.name WHERE projects.owner_id = excluded.owner_id RETURNING id, name",
  [id, ownerId, name],
);
const updated = await db.query(
  "UPDATE projects SET name = ? WHERE id = ? AND owner_id = ? RETURNING id, name",
  [name, id, ownerId],
);
const deleted = await db.query(
  "DELETE FROM projects WHERE id = ? AND owner_id = ? RETURNING id, name",
  [id, ownerId],
);
~~~

The upsert cannot take ownership of another user's existing row. Decide how the
app reports its zero-row result without revealing another tenant's resource.
For child writes, include an EXISTS check against the authorized parent.
When no returned rows are needed, use execute and inspect rowsAffected.
Bulk inserts use a bounded batch or a multi-row parameterized INSERT, preserving
all-or-nothing requirements and the [SQL limits](https://docs.terrascale.tech/guides/sql-http#limits).

## Exact count alongside a page

Before: select("id,name", { count: "exact" }).range(start,end).
After: batch count and page together so the count is not based on a later request.

~~~ts
const results = await db.batch([
  {
    sql: "SELECT id, name FROM projects WHERE owner_id = ? ORDER BY id LIMIT ? OFFSET ?",
    args: [ownerId, pageEnd - pageStart + 1, pageStart],
  },
  { sql: "SELECT COUNT(*) AS n FROM projects WHERE owner_id = ?", args: [ownerId] },
]);
const data = results[0].rows.map(row => ({ id: row[0], name: row[1] }));
const count = Number(results[1].rows[0][0]);
~~~

Validate integer conversion when counts can exceed the app's safe numeric range.
A head-only count omits the first statement. Preserve identical predicates;
do not add LIMIT/OFFSET to COUNT(*). There is no documented planned/estimated
PostgREST count equivalent in [SQL HTTP](https://docs.terrascale.tech/guides/sql-http).

## RPC and transaction semantics

Before: rpc("complete_project", { project_id: projectId }) is an application RPC
whose inspected definition marks all tasks complete for an authorized owner and
returns their IDs. After: replace that specific definition, not just its name.

~~~ts
const completed = await db.query(
  "UPDATE tasks SET done = 1 WHERE project_id = ? AND EXISTS (SELECT 1 FROM projects p WHERE p.id = tasks.project_id AND p.owner_id = ?) RETURNING id",
  [projectId, ownerId],
);
~~~

This is valid only for that definition. Port additional checks and side effects
explicitly. Several independent writes forming one RPC must use one batch;
a read-modify-write RPC needs a conditional statement or a documented transaction
path, not two unprotected requests ([SQL HTTP](https://docs.terrascale.tech/guides/sql-http)).

## Errors, browser integration and live state

Replace checks of result.error with try/catch at the app boundary:

~~~ts
try {
  await db.execute("UPDATE projects SET name = ? WHERE id = ? AND owner_id = ?", [name, id, ownerId]);
} catch (error) {
  if (error instanceof SqlError && error.code === "unique_violation") {
    throw new Error("Name already exists");
  }
  throw error;
}
~~~

Use code/sqlstate/statement for decisions, sanitize client errors and retain
request IDs for diagnosis. An uncertain commit needs reconciliation before another
write ([errors](https://docs.terrascale.tech/guides/errors)).

For a live table UI, replace channel postgres_changes callbacks that refetch data
with a shape snapshot subscription. This example runs with the server client above;
browser clients need the scoped tokens/rules described in the entrypoint.

~~~ts
function watchProjects(
  onRows: (rows: { id: string; name: string }[]) => void,
  onError: (error: unknown) => void,
): () => void {
  const subscription = db.shape("projects", {
    columns: ["id", "name", "owner_id"],
  }).subscribe(({ rows }) => {
    onRows([...rows.values()]
      .filter(row => row.owner_id === ownerId)
      .map(row => ({ id: String(row.id), name: String(row.name) })));
  });
  void subscription.done.catch(onError);
  return () => subscription.close();
}
~~~

Pass the rows to the app's rendering/delivery callback and register the returned
cleanup function on teardown. The done promise reports terminal errors. This
example assumes project ownership is the complete read policy; implement any
additional restrictions before delivery. Never forward other users' rows.
For browser reads use rules, not a privileged client with a client-side filter.
The documented where option can narrow the shape with a fixed reviewed predicate.
Do not interpolate untrusted values into where: its documented API has no args map.
Shapes deliver row maps, not Supabase event payloads; broadcast and presence need
the gateway paths in the entrypoint. See
[live shapes](https://docs.terrascale.tech/concepts/sql#live-shapes-and-rules).

The SKILL entrypoint covers auth, storage, functions, environment removal and the
verification/cutover checklist; follow those steps before deleting Supabase.
