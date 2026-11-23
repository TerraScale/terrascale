# Turso client and feature reference

Read this before choosing a schema and replacing `@libsql/client`.
Return to the [workflow](../SKILL.md) for ordering, verification and rollback.

## Feature mapping

“Not documented” is a migration blocker to resolve, not a promise of compatibility.
TerraScale SQL support below comes from [SQL](https://docs.terrascale.tech/concepts/sql)
and [import](https://docs.terrascale.tech/guides/import).

| Source feature | TerraScale equivalent or workaround |
| --- | --- |
| Database-per-tenant fleet and groups | Map each tenant to a separately scoped database; keep group membership in the routing manifest. Do not assume a group token transfers. See [scope application data](https://docs.terrascale.tech/blog/database-per-customer). |
| Remote SQL with `@libsql/client` | Replace the client with documented destination SDK calls or [SQL over HTTP](https://docs.terrascale.tech/guides/sql-http). Result shapes differ. |
| Embedded replica, `file:` URL, `syncUrl`, `.sync()` | No attached or replicated local file in the [import contract](https://docs.terrascale.tech/guides/import). Query remotely; implement an application cache with explicit freshness if needed. Offline operation requires a separate design. |
| `batch(statements, "write")` | One destination HTTP request is an atomic batch; use `db.batch(statements)`, bounded by [limits](https://docs.terrascale.tech/limits). |
| Long or interactive transactions | Do not carry transaction-mode strings into HTTP. Use a bounded batch, or the documented WebSocket/Postgres wire session with its 5-second transaction limit; see [SQL transactions](https://docs.terrascale.tech/concepts/sql). |
| `vector`/`F32_BLOB` columns, `vector32`, `vector_top_k`, `libsql_vector_idx` | No equivalent vector functions/indexes are documented in [SQL](https://docs.terrascale.tech/concepts/sql). Preserve validated raw values as `BLOB` or normalized JSON `TEXT`; move similarity search to an external service or application code. Storing bytes does not preserve vector search behavior. |
| Cross-database `ATTACH` | Refused by [SQL](https://docs.terrascale.tech/concepts/sql). Query the databases separately and join in application code, or deliberately co-locate the relevant tables. There is no cross-database atomicity implied by that workaround. |
| Turso database/group tokens | Create destination API keys with explicit scopes/database restrictions; exchange for access tokens. See [authentication](https://docs.terrascale.tech/guides/authentication). |
| Turso branch database | Use independent snapshot [branches](https://docs.terrascale.tech/guides/branches), with explicit TTL and database capacity; migrate existing divergent branches independently. |
| Source recovery history | SQL transfers current schema/data, not source recovery history. Establish and test destination [backups](https://docs.terrascale.tech/guides/backups-and-restore). |

Source behavior: [TypeScript client](https://docs.turso.tech/sdk/ts/reference),
[embedded replicas](https://docs.turso.tech/features/embedded-replicas/introduction),
[vectors](https://docs.turso.tech/features/ai-and-embeddings),
[ATTACH](https://docs.turso.tech/features/attach-database), and
[branching](https://docs.turso.tech/features/branching).

## Client before and after

Assume `customers(id TEXT PRIMARY KEY, email TEXT NOT NULL)` exists and `id` and
`email` are validated application inputs. Keep the existing authorization check
before selecting a tenant’s database handle.

Before, using the [libSQL client API](https://docs.turso.tech/sdk/ts/reference):

```js
import { createClient } from "@libsql/client";

const source = createClient({
  url: process.env.TURSO_DATABASE_URL,
  authToken: process.env.TURSO_AUTH_TOKEN,
});
const selected = await source.execute({
  sql: "SELECT id, email FROM customers WHERE id = ?",
  args: [id],
});
const changed = await source.batch([
  { sql: "UPDATE customers SET email = ? WHERE id = ?", args: [email, id] },
  { sql: "SELECT id, email FROM customers WHERE id = ?", args: [id] },
], "write");
```

After, using the API documented by the
[JavaScript quickstart](https://docs.terrascale.tech/getting-started/javascript):
that page explicitly targets source `0.0.19`, while published `0.0.17` is the
previous API. These calls require the source checkout install shown there:

```bash
npm install /path/to/terrascale/sdk/js
```

Use an actual reviewed checkout path. Do not present `npm install @terrascale/sdk`
as sufficient for this example; if a source install is unsuitable, use the stable
[HTTP contract](https://docs.terrascale.tech/guides/sql-http) instead.

```js
import { createTerraBaseV1Client } from "@terrascale/sdk";

const client = createTerraBaseV1Client({
  baseUrl: "https://api.terrascale.tech",
  project: process.env.TERRASCALE_PROJECT_ID,
  apiKey: process.env.TERRASCALE_API_KEY,
});
const db = client.database(process.env.TERRASCALE_DATABASE_NAME);
const selected = await db.query(
  "SELECT id, email FROM customers WHERE id = ?", [id],
);
const changed = await db.batch([
  { sql: "UPDATE customers SET email = ? WHERE id = ?", args: [email, id] },
  { sql: "SELECT id, email FROM customers WHERE id = ?", args: [id] },
]);
const updatedRow = changed[1].rows[0];
```

`query` returns objects keyed by column name; batch rows are arrays, with column
metadata in the result. `execute(sql, args)` runs a single statement. Named
arguments become `{sql, named: {name: value}}`, rather than a source `args` map.
The client exchanges `apiKey` for access tokens. These signatures, result shapes,
and key handling are documented in the
[quickstart](https://docs.terrascale.tech/getting-started/javascript).

Handle `SqlError.code`, `sqlstate`, and `statement`; never assume source error
strings or `rowsAffected` property names transfer. The quickstart says nothing is
retried automatically. For uncertain write outcomes, reconcile the operation
before replaying it; see [errors](https://docs.terrascale.tech/guides/errors).
Use `bigint` for large integers with this SDK; avoid conversion to JavaScript
`number` when validating 64-bit values, as explained in the quickstart.

## Prepare the CLI artifact

The [public import guide](https://docs.terrascale.tech/guides/import) shows raw
`.dump` input, but the reviewed CLI contradicts that shortcut:
[import.go](https://github.com/TerraScale/terrascale/blob/6b786568d60373e2fa16836a8ab4354bbb642651/sdk/go/cmd/ts/import.go#L31)
forwards split statements unchanged, and
[splitSQL](https://github.com/TerraScale/terrascale/blob/6b786568d60373e2fa16836a8ab4354bbb642651/sdk/go/cmd/ts/migrate.go#L398)
splits trigger bodies at their internal semicolons. The
[HTTP contract](https://docs.terrascale.tech/guides/sql-http) refuses transaction
wrappers; [SQL](https://docs.terrascale.tech/concepts/sql) always enforces foreign
keys. Do not assume the CLI strips controls or preserves complete trigger DDL.

Before any CLI import, use a SQL-aware parser or review complete statements:

1. Keep the original successful export unchanged. Reject truncated/failed dumps
   and dumps that end in `ROLLBACK`; do not turn an aborted dump into a partial load.
2. Remove complete top-level dump `BEGIN`/`COMMIT` controls and all source `PRAGMA`
   statements from the import copy. Inspect each removed pragma for behavior that
   needs a schema/load-order change. Never strip words inside strings or triggers.
3. Extract every complete `CREATE TRIGGER ... BEGIN ... END;` to its own reviewed
   file. Do not put trigger DDL into the CLI artifact, even if it is supported SQL.
4. Produce `source.sql` containing reviewed tables, inserts, indexes and views,
   ordered for enforced foreign keys. Resolve other unsupported source statements.
   Verify there are no dump controls, pragmas or trigger declarations left.
5. Rehearse this exact artifact through the installed CLI into an empty target.
   After importing and verifying rows, submit each complete trigger as one SDK/HTTP
   statement, within the [statement limits](https://docs.terrascale.tech/limits).
   Keep separate progress for trigger installation; a failed DDL call does not undo
   already committed data batches.

For example, given source dump controls, an audit table, a customer table, data,
and an audit trigger, the prepared CLI artifact can contain only:

```sql
CREATE TABLE customers (id TEXT PRIMARY KEY, email TEXT NOT NULL);
CREATE TABLE audit (id TEXT NOT NULL);
INSERT INTO customers VALUES ('c1', 'team@example.com');
```

After row verification, submit the entire following string as one statement.
Its internal semicolon stays inside the string sent to the SQL endpoint:

```js
await db.execute(
  "CREATE TRIGGER customer_audit AFTER INSERT ON customers BEGIN INSERT INTO audit(id) VALUES (new.id); END;",
);
```

Here `db` is the destination SDK handle from the before/after example, with its
source-checkout prerequisite. Install triggers once in the migration phase, then
verify subsequent customer inserts create exactly one expected audit row. Check
for an existing trigger before resuming an uncertain DDL outcome.

## Fleet copy

Create a protected, headerless `fleet.tsv` with exactly three tab-separated columns:
source database name, precreated empty target name, and a unique artifact basename.
Example nonsecret routing data:

```text
tenant-a	customer-a	customer-a
tenant-b	customer-b	customer-b
```

Validate uniqueness and legal names before running; verify that the manifest
covers the full inventory and that every target is in `TERRASCALE_PROJECT_ID`.
Keep a progress ledger with export/prepare/import/triggers/verify/cutover states.
First export the frozen fleet; this loop stops on failure and never imports raw SQL.

```bash
set -euo pipefail
umask 077
: "${MIGRATION_DIR:?}" "${TERRASCALE_PROJECT_ID:?}"
mkdir -p "$MIGRATION_DIR"
while IFS=$'\t' read -r source target artifact; do
  test -n "$source" && test -n "$target" && test -n "$artifact"
  [[ "$artifact" =~ ^[a-z][a-z0-9_-]*$ ]]
  test ! -e "$MIGRATION_DIR/$artifact.original.sql"
  turso db shell "$source" .dump > "$MIGRATION_DIR/$artifact.partial"
  mv "$MIGRATION_DIR/$artifact.partial" "$MIGRATION_DIR/$artifact.original.sql"
  printf 'exported\t%s\n' "$source"
done < fleet.tsv
```

The partial filename prevents treating an interrupted export as a finished dump.
Apply the preparation checklist above to **each** `$artifact.original.sql`, writing
`$artifact.prepared.sql` plus separate complete trigger files. Verify controls and
triggers are absent from every prepared artifact and rehearse their CLI parsing.
Only then run this second pass in the same shell, still with sources frozen:

```bash
while IFS=$'\t' read -r source target artifact; do
  [[ "$artifact" =~ ^[a-z][a-z0-9_-]*$ ]]
  test -s "$MIGRATION_DIR/$artifact.prepared.sql"
  ts -project "$TERRASCALE_PROJECT_ID" db import "$target" "$MIGRATION_DIR/$artifact.prepared.sql"
  printf 'loaded\t%s\t%s\n' "$source" "$target"
done < fleet.tsv
```

Verify rows for each target, then install its separate complete triggers through
SDK/HTTP as above and verify their behavior before marking the tenant verified.
On failure inspect the ledger and artifacts, then resume only the unfinished
manifest rows under the workflow’s partial-import rules. The target may already
contain committed batches even though the loop stopped. Never mark `loaded` as
`verified` or switch routing automatically.

The commands use [Turso shell export](https://docs.turso.tech/cli/db/shell) and
[TerraScale import](https://docs.terrascale.tech/guides/import), with
[explicit project selection](https://docs.terrascale.tech/cli).
