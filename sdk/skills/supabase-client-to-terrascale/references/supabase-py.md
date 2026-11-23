# Supabase Python to TerraScale

Use this reference for supabase-py repositories, jobs and Python web backends.
Examples assume migrated projects(id, owner_id, name) and
tasks(id, project_id, title, done, created_at); IDs are TEXT and done is integer.
owner_id is resolved from a validated server session and destination identity
mapping; all other inputs are validated by the app before these snippets run.
Use the same schema, constraints, nullability and authorization as the source app.

Sources: [Supabase Python](https://supabase.com/docs/reference/python),
[select](https://supabase.com/docs/reference/python/select),
[filters](https://supabase.com/docs/reference/python/using-filters),
[RPC](https://supabase.com/docs/reference/python/rpc).
Destination source: [Python quickstart](https://docs.terrascale.tech/getting-started/python),
[SQL HTTP](https://docs.terrascale.tech/guides/sql-http) and
[SQL](https://docs.terrascale.tech/concepts/sql).

API audit: TerraScale origin/main at da3382de73201d4fc94dd6872fd2cad5a97de12f,
including sdk/python/src/terrascale/__init__.py, client.py, sql.py and aio.py.
Recheck the quickstart and these definitions when changing the pinned SDK.

## Construction, lifecycle and row shapes

Before:

~~~python
import os
from supabase import create_client

supabase = create_client(os.environ["SUPABASE_URL"], os.environ["SUPABASE_KEY"])
~~~

After: use the source install documented in the quickstart; pin the reviewed commit
in the app lockfile instead of treating a moving branch as a production pin.

~~~sh
python -m pip install "git+https://github.com/TerraScale/terrascale@main#subdirectory=sdk/python"
~~~

~~~python
import os
from terrascale import TerraScale, SqlError

with TerraScale() as ts:
    db = ts.database(os.environ["TERRASCALE_DATABASE_NAME"])
    rows = db.query("SELECT id, name FROM projects WHERE owner_id = ?", [owner_id])
    data = [dict(row) for row in rows]
~~~

TerraScale reads TERRASCALE_PROJECT_ID and TERRASCALE_API_KEY and exchanges the
key. Keep it server-only. Remaining sync snippets run inside this context, with
db alive for the request/job lifetime. Do not return a db handle after closing ts.
query returns Row objects, not a response.data envelope; dict(row) constructs
a plain dictionary, while get_int64/get_string read typed values. execute returns
StatementResult with rows, rows_affected and last_insert_rowid; batch returns
one StatementResult per statement
([quickstart](https://docs.terrascale.tech/getting-started/python)).

## Builder translation map

| supabase-py | SQL/application equivalent |
| --- | --- |
| table("projects").select("id,name").execute() | db.query("SELECT id, name FROM projects ...", args) |
| eq / neq / gt / gte / lt / lte | = / <> / > / >= / < / <= with bound arguments. |
| match({"a":a,"b":b}) | a = ? AND b = ?; null requires IS NULL. |
| is_("x", "null") / not_.is_("x", "null") | x IS NULL / x IS NOT NULL. |
| in_("id", ids) | Generate only placeholders, bind each value; handle [] as no matches. |
| like / ilike | LIKE with a bound pattern; verify case/Unicode differences. |
| or_("a.eq.x,b.eq.y") | (a = ? OR b = ?) inside the owner/tenant predicate. |
| filter / not_ | Review the real operator; no raw PostgREST filter string in SQL. |
| select("id,tasks(id,title)") | LEFT JOIN plus nested dict/list assembly; !inner requires INNER JOIN. |
| insert(...).execute() with returned data | INSERT ... RETURNING ... through query; project requested DTO fields in the app. |
| upsert(..., on_conflict="id") | INSERT ... ON CONFLICT(id) DO UPDATE ... RETURNING ... . |
| ignore_duplicates=True | ON CONFLICT(the_unique_key) DO NOTHING. |
| update / delete | UPDATE / DELETE with all filters and explicit authorization. |
| order / limit / range | ORDER BY with a unique tie-breaker, LIMIT ? OFFSET ?. |
| select(..., count="exact", head=True) | COUNT(*) with identical filters; no estimated/planned count mapping. |
| single / maybe_single | Fetch at most two matches and enforce cardinality explicitly. |
| rpc(...).execute() | Reviewed SQL, batch or backend logic based on the RPC definition. |

Bind only values; identifiers and sorting must come from fixed code or allowlists.
JSON is TEXT with the documented JSON functions. PostgreSQL arrays, range/full-text
operators, extensions and custom functions need a separate schema/query design,
not a guessed replacement ([SQL](https://docs.terrascale.tech/concepts/sql)).

## Select, pagination, count and single-row behavior

Before (separate operations):

~~~python
response = (supabase.table("projects").select("id,name").eq("owner_id", owner_id)
            .ilike("name", pattern).order("name").order("id")
            .range(page_start, page_end).execute())
data = response.data
one = supabase.table("projects").select("id,name").eq("id", project_id).single().execute()
~~~

After: range endpoints are inclusive. Validate offsets and maximum page size.
Choose explicit null ordering where the source specified it.

~~~python
rows = db.query(
    "SELECT id, name FROM projects WHERE owner_id = ? AND name LIKE ? ORDER BY name, id LIMIT ? OFFSET ?",
    [owner_id, pattern, page_end - page_start + 1, page_start],
)
data = [dict(row) for row in rows]
matches = db.query(
    "SELECT id, name FROM projects WHERE id = ? AND owner_id = ? LIMIT 2",
    [project_id, owner_id],
)
if len(matches) != 1:
    raise ValueError("Expected exactly one row")
one = dict(matches[0])
~~~

For maybe_single, return None on zero, the mapped row on one, and an error on more
than one. Preserve the app's domain error/status handling instead of inventing
Supabase-compatible SDK exceptions.

Before: select("id,name", count="exact").range(start,end).execute().
After: count and page in one atomic request, with exactly matching predicates.

~~~python
page, total = db.batch([
    ("SELECT id, name FROM projects WHERE owner_id = ? ORDER BY id LIMIT ? OFFSET ?",
     [owner_id, page_end - page_start + 1, page_start]),
    ("SELECT COUNT(*) AS n FROM projects WHERE owner_id = ?", [owner_id]),
])
data = [dict(row) for row in page.rows]
count = total.rows[0].get_int64("n")
~~~

For a head-only count run only COUNT(*). Do not count the limited page.
The atomic batch contract is in [SQL HTTP](https://docs.terrascale.tech/guides/sql-http).

## Relationship selects and response assembly

Before:

~~~python
response = (supabase.table("projects").select("id,name,tasks(id,title)")
            .eq("owner_id", owner_id).execute())
~~~

After:

~~~python
flat = db.query(
    "SELECT p.id, p.name, t.id AS task_id, t.title AS task_title FROM projects p LEFT JOIN tasks t ON t.project_id = p.id WHERE p.owner_id = ? ORDER BY p.id, t.id",
    [owner_id],
)
grouped = {}
for row in flat:
    project = grouped.setdefault(row["id"], {"id": row["id"], "name": row["name"], "tasks": []})
    if row["task_id"] is not None:
        project["tasks"].append({"id": row["task_id"], "title": row["task_title"]})
data = list(grouped.values())
~~~

For many-to-one embeddings use a nested object or None. Preserve aliases, child
filters, child RLS and !inner semantics. Put child authorization on the JOIN where
appropriate; a child WHERE condition can eliminate parents without children.
Page parents before joining, or flattened LIMIT changes parent/child counts.

## Mutations and RPC

Before (independent examples):

~~~python
supabase.table("projects").insert({"id": project_id, "owner_id": owner_id, "name": name}).execute()
supabase.table("projects").upsert({"id": project_id, "owner_id": owner_id, "name": name}, on_conflict="id").execute()
supabase.table("projects").update({"name": name}).eq("id", project_id).execute()
supabase.table("projects").delete().eq("id", project_id).execute()
~~~

After:

~~~python
inserted = db.query(
    "INSERT INTO projects (id, owner_id, name) VALUES (?, ?, ?) RETURNING id, name",
    [project_id, owner_id, name],
)
upserted = db.query(
    "INSERT INTO projects (id, owner_id, name) VALUES (?, ?, ?) ON CONFLICT(id) DO UPDATE SET name = excluded.name WHERE projects.owner_id = excluded.owner_id RETURNING id, name",
    [project_id, owner_id, name],
)
updated = db.execute(
    "UPDATE projects SET name = ? WHERE id = ? AND owner_id = ?",
    [name, project_id, owner_id],
)
deleted = db.execute(
    "DELETE FROM projects WHERE id = ? AND owner_id = ?",
    [project_id, owner_id],
)
~~~

Treat snippets as independent operations; running all four consecutively deletes
the row just inserted. Inspect rows_affected for no-return mutations. Use query
with RETURNING when callers consume response.data from a mutation; Python writes
can return representations without a chained select. Preserve actual conflict
targets, omitted-field defaults and ownership on upsert; a foreign-owner conflict
must not take ownership. Child writes need an authorized-parent EXISTS predicate.

Before: rpc("complete_project", {"project_id": project_id}).execute().
Suppose inspection proves this RPC only marks owned-project tasks complete and
returns their IDs. That specific definition becomes:

~~~python
completed = db.query(
    "UPDATE tasks SET done = 1 WHERE project_id = ? AND EXISTS (SELECT 1 FROM projects p WHERE p.id = tasks.project_id AND p.owner_id = ?) RETURNING id",
    [project_id, owner_id],
)
~~~

Port extra validation/side effects from the real definition. Use one batch for
dependent writes; two execute calls are not one transaction. Interactive work
needs the documented session/ORM path and its limits, not BEGIN over HTTP
([SQL HTTP](https://docs.terrascale.tech/guides/sql-http)).

## Async handlers, errors and service handoffs

Do not block an async web handler with the synchronous client. The source SDK
exports AsyncTerraScale and async database operations; await them and close the
client with async with ([Python SDK source](https://github.com/TerraScale/terrascale/blob/main/sdk/python/src/terrascale/aio.py),
[SDK guide](https://docs.terrascale.tech/guides/sdks)).

~~~python
from terrascale import AsyncTerraScale

async def owned_projects(owner_id: str):
    async with AsyncTerraScale() as ts:
        db = ts.database(os.environ["TERRASCALE_DATABASE_NAME"])
        rows = await db.query("SELECT id, name FROM projects WHERE owner_id = ?", [owner_id])
        return [dict(row) for row in rows]
~~~

Catch SqlError at the app boundary and map code/sqlstate/statement to domain
errors. Keep other exceptions visible; sanitize HTTP responses. Do not blindly
repeat an uncertain mutation
([errors](https://docs.terrascale.tech/guides/errors)).
For SQLAlchemy applications, prefer the
[documented dialect](https://docs.terrascale.tech/guides/orm-sqlalchemy) over
maintaining a second query layer.

Follow the entrypoint for auth, file storage and functions replacement, realtime
workarounds, env changes, verification, cutover and rollback. Python jobs reacting
to every database event need a durable application outbox/worker; a shape is live
row state rather than an assumed Supabase event stream.
