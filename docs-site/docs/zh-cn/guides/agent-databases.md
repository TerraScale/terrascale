---
title: Agent databases
description: Give each agent, session or test run its own database that expires on a TTL, fork it, and hand the agent a key that reaches only that database.
---

<!-- i18n:fallback:zh-cn -->
> **翻译待审。** 此页面尚无经人工审校的中文译文，暂显示英文原文。

<div lang="en" dir="ltr">

# Agent databases

Give every agent, session or test run its own database. Create it with a time to live, fork it from a source or from a
point in time, hand the agent a key that reaches only that database, and let it expire or delete it when the work ends.

Shared names and method signatures for every SDK are in [SQL API](https://github.com/TerraScale/terrascale/blob/main/sdk/SQL-API.md#databases-for-agents).
Requests below use `https://api.terrascale.tech`, with `Authorization: Bearer <token>` from the
[authentication guide](/zh-cn/guides/authentication). Replace `{project}` with your project ID and `{database}` with a database name or ULID.

## Use MCP from Claude Code or Codex

Connect to `https://mcp.terrascale.tech/` using a project API key with
`databases:read databases:write sql:read sql:write`. Create the parent key in
[the dashboard](https://dash.terrascale.tech/api-keys) and complete its hosted confirmation once.
The key stays in your client's environment; never put it in a prompt or commit it in configuration.

Enter it in your terminal without recording its value in shell history:

```bash
read -rs TERRASCALE_API_KEY && export TERRASCALE_API_KEY
```

### Claude Code

```bash
claude mcp add --transport http terrascale https://mcp.terrascale.tech/ \
  --header 'Authorization: Bearer ${TERRASCALE_API_KEY}'
claude
```

Keep the single quotes so Claude Code reads the variable from its environment when connecting.

### Codex

Add this to `~/.codex/config.toml`, then start Codex from the terminal where you exported the key:

```toml
[mcp_servers.terrascale]
url = "https://mcp.terrascale.tech/"
bearer_token_env_var = "TERRASCALE_API_KEY"
```

See the [Codex MCP configuration documentation](https://developers.openai.com/codex/mcp/).
For interactive dashboard access, both clients also support [MCP sign-in](/zh-cn/guides/mcp).
OAuth and personal access tokens can manage databases and run SQL using your dashboard permissions;
scoped-key minting requires the parent API key connection above.

### Agent workflow

Call `tools/list` to discover the current tools, then `get_context` to read the key's project and scopes.
The following tool calls create a temporary database, fork it, mint a scoped key, query and inspect it,
restore a backup, and delete the databases. Substitute the IDs returned by earlier calls:

| Tool | Arguments |
| --- | --- |
| `create_database` | `{"name":"agent-run","ttlHours":1}` |
| `execute_sql` | `{"databaseId":"<run-id>","sql":"CREATE TABLE results (id TEXT PRIMARY KEY, value TEXT)"}` |
| `execute_sql` | `{"databaseId":"<run-id>","sql":"INSERT INTO results VALUES ('one', 'ready')"}` |
| `fork_database` | `{"databaseId":"<run-id>","name":"agent-fork","ttlHours":0.25}` |
| `create_scoped_key` | `{"databaseId":"<fork-id>","access":"read_write","ttlSeconds":600}` |
| `run_sql` | `{"databaseId":"<fork-id>","sql":"SELECT * FROM results"}` |
| `get_database_schema` | `{"databaseId":"<fork-id>"}` |
| `delete_database` | `{"databaseId":"<fork-id>","confirmName":"agent-fork"}` |
| `create_backup` | `{"databaseId":"<run-id>"}` |
| `restore_backup` | `{"databaseId":"<run-id>","backupId":"<backup-id>","name":"agent-restored"}` |
| `delete_database` | `{"databaseId":"<restored-id>","confirmName":"agent-restored"}` |
| `delete_database` | `{"databaseId":"<run-id>","confirmName":"agent-run"}` |

MCP database TTLs use `ttlHours`; scoped keys use `ttlSeconds`.
Fork and restore each need room for another database under the organization's plan. Free allows one database;
use a plan with capacity for the source and its copy.

`execute_sql` runs one read or write statement and returns columns, rows and write counts.
`run_sql` accepts reads only. `get_database_schema` includes empty tables, declared columns and types;
it returns at most 100 tables and reports `truncated` when more exist.

To hand work to a restricted agent, save the returned scoped `key` in that agent's protected environment
and configure a separate MCP connection with it. A `read` key can query and inspect its database but
`execute_sql` refuses writes. A `read_write` key can also change its tables and data. Neither scoped key
can create, fork, restore or delete databases, mint keys, or access another database. The parent key
keeps lifecycle authority. Scoped keys and their secrets are never tool arguments.

## Manage schema as code

Use the `ts` CLI to keep a database's SQL schema in version control:

```sh
ts schema pull --database agent-run
# Edit the CREATE statements in schema.sql.
ts schema diff schema.sql --database agent-run
ts schema apply schema.sql --database agent-run --dry-run
ts schema apply schema.sql --database agent-run
```

`pull` writes `schema.sql` in the current directory and refuses to overwrite an existing file.
The file contains the database's tables, indexes, views and triggers, without data or internal objects.
Keep every object you want to retain in the file: omitting one plans its removal. An empty file plans removal of every SQL object.

`diff` and `apply --dry-run` print the DDL needed to reach the desired schema without writing to the database.
Whitespace and comments do not create changes. The planner supports creating and dropping
objects, replacing indexes, views and triggers, and adding or dropping table columns. Changes to an existing
column's type, default or constraints, table constraints, table options and column order require an explicit
[`ts migrate` migration](/zh-cn/guides/migrations). Unsupported SQL is refused before any live write.

`apply` submits the entire plan through the public SQL REST API in one transaction. A failed statement rolls it
all back. A concurrent schema edit aborts the plan; pull or diff again before retrying.
Any plan containing `DROP`, including column drops and index, view or trigger replacement, requires `--allow-drop`:

```sh
ts schema apply schema.sql --database agent-run --allow-drop
```

Dropped tables and columns lose their data. Renaming an object in the file is a drop and a creation; use a migration
to preserve data during a rename. Column additions must be valid for existing rows: for example, a new `NOT NULL`
column needs a non-null default. The server enforces constraints during apply.

For agents and CI, provide `TERRASCALE_API_KEY` through your secret store and select the database explicitly:

```sh
ts --json schema diff schema.sql --database agent-run
ts --json schema apply schema.sql --database agent-run --dry-run
ts --json schema apply schema.sql --database agent-run
```

JSON plans contain `statements` (DDL strings), `destructive` and `applied`. A refused drop returns
`schema_drop_refused` and exits with status `1`. An unchanged schema returns an empty `statements` array.
Pull and diff need `sql:read`; applying DDL also needs `sql:write`. API-key commands use the key's
project; organization keys also need `--project <id>`. Signed-in callers can omit `--database` after saving a
database with `ts context set <org> <project> <database>`.

Schemas are limited to 1 MiB, each statement to 16 KiB, and each apply plan to 255 DDL statements.

## Create a database that deletes itself

`POST /v1/projects/{project}/databases` with scope `databases:write`:

```http
POST /v1/projects/{project}/databases
Content-Type: application/json

{"name":"run-42","ttlSeconds":3600}
```

The response is `201` with the database and `expiresAt` (RFC 3339). `ttlSeconds` is a whole number from 60 to
31,536,000 (365 days). Without it, `expiresAt` is `null` and the database stays until you delete it.

- The database counts against your plan's database limit and is billed until it is deleted.
- At `expiresAt`, TerraScale deletes the database, its backups and its data keys. Deletion happens within seconds after
  `expiresAt` and never before. The data cannot be recovered afterwards.

## Fork a database

Fork from now, which gives a consistent copy of the source taken at the time of the request:

```http
POST /v1/projects/{project}/databases/{source}/branches
Content-Type: application/json

{"name":"run-42-fork","ttlSeconds":600}
```

Or fork from a point in time inside the source's restore window. The fork holds every transaction committed before
`pointInTime`:

```http
POST /v1/projects/{project}/databases/{source}/point-in-time/restore
Content-Type: application/json

{"name":"run-42-restored","pointInTime":"2026-11-03T12:00:00Z","ttlSeconds":600}
```

`ttlSeconds` is optional on both. A fork does not inherit the source's TTL: set one on the fork if you want it to expire.

## Hand the agent a scoped key

Mint a key from your own API key. It reaches one database, with `read` (`sql:read`) or `read_write`
(`sql:read sql:write`) access:

```http
POST https://auth.terrascale.tech/v1/terrascale/api-keys/self/scoped-keys
Authorization: Basic <base64(keyId:key)>
Content-Type: application/json

{"project":"{project}","database":"<database-ulid>","access":"read_write","ttl_seconds":600}
```

The response is `201` with `key`, `id`, `project_id`, `database`, `access` and `expires_at`. `database` is the database
ULID, not its name. `ttl_seconds` is a whole number from 60 to 2,592,000 (30 days), and defaults to 3600. The key never
outlives your key. `project` is required for an organization key and must match the project for a project key.

Give the agent only the `key` value. It is shown once, and it is an ordinary API key for that agent's client.

- The agent cannot create, fork, change the TTL of or delete databases, and it cannot mint keys.
- Requests for any other database return `404`.
- Revoking or rotating your key revokes every scoped key it minted.

## Change or clear the TTL

```http
PUT /v1/projects/{project}/databases/{database}/ttl
Content-Type: application/json

{"ttlSeconds":7200}
```

```http
DELETE /v1/projects/{project}/databases/{database}/ttl
```

`PUT` sets `expiresAt` to now plus `ttlSeconds`. `DELETE` clears it, so the database stays until you delete it.

## Delete a database

```http
DELETE /v1/projects/{project}/databases/{database}
```

The response is `204`. Deletion removes the data, backups and data keys immediately, the same as expiry.

## SDK quickstart

Each example creates an agent database, forks it, mints a read-write key for the agent, sets the TTL on the run and deletes the
run. Read your API key from the environment, not from source code.

### JavaScript and TypeScript

```js
import { createTerraBaseClient } from "@terrascale/sdk";

const project = process.env.TERRASCALE_PROJECT;
const client = createTerraBaseClient({
  baseUrl: "https://api.terrascale.tech",
  project,
  apiKey: process.env.TERRASCALE_API_KEY,
});

const run = await client.createDatabase(project, "agent_run", { ttlSeconds: 3600 });
const fork = await client.forkDatabase(project, run.id, "agent_fork", { ttlSeconds: 600 });
const key = await client.createScopedKey({ project, database: fork.id, access: "read_write", ttlSeconds: 600 });

const agent = createTerraBaseClient({ baseUrl: "https://api.terrascale.tech", project, apiKey: key.key });
await agent.database(fork.id).execute("CREATE TABLE results (id TEXT PRIMARY KEY, value TEXT)");

await client.setDatabaseTtl(project, run.id, 7200);
await client.clearDatabaseTtl(project, run.id);
await client.deleteDatabase(project, run.id);
```

`forkDatabase` also accepts `{ pointInTime: "2026-11-03T12:00:00Z", ttlSeconds: 600 }`.

### Python

```python
from terrascale import TerraScale

with TerraScale() as ts:
    run = ts.create_database("agent_run", ttl_seconds=3600)
    fork = ts.fork_database(run.id, "agent_fork", ttl_seconds=600)
    key = ts.create_scoped_key(fork.id, "read_write", ttl_seconds=600)

    with TerraScale(key.key, project=ts.project) as agent:
        agent.database(fork.id).execute("CREATE TABLE results (id TEXT PRIMARY KEY, value TEXT)")

    ts.set_database_ttl(run.id, 7200)
    ts.clear_database_ttl(run.id)
    ts.delete_database(run.id)
```

`fork_database(..., point_in_time="2026-11-03T12:00:00Z")` restores from a point in time.

### C# / .NET

```csharp
var sql = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions { Region = "gru", ApiKey = apiKey });

TerraBaseManagedDatabase run = await sql.CreateDatabaseAsync(projectId, "agent_run", ttlSeconds: 3600);
TerraBaseManagedDatabase fork = await sql.ForkDatabaseAsync(projectId, run.Id, "agent_fork", ttlSeconds: 600);
TerraBaseScopedKey key = await sql.CreateScopedKeyAsync(fork.Id, "read_write", projectId, ttlSeconds: 600);

var agent = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions { Region = "gru", ApiKey = key.Key });
await agent.Database(projectId, fork.Id).ExecuteAsync("CREATE TABLE results (id TEXT PRIMARY KEY, value TEXT)");

await sql.SetDatabaseTtlAsync(projectId, run.Id, 7200);
await sql.ClearDatabaseTtlAsync(projectId, run.Id);
await sql.DeleteDatabaseAsync(projectId, run.Id);
```

`ForkDatabaseAsync` also takes `pointInTime` to fork from an instant inside the source's restore window.

### Go

```go
package main

import (
	"context"
	"time"

	tb "terrascale.tech/sdk/v1"
)

func run(ctx context.Context, projectID, apiKey string) error {
	c, err := tb.New(tb.Config{APIKey: apiKey})
	if err != nil {
		return err
	}
	ttl := int64(3600)
	forkTTL := int64(600)

	db, err := c.CreateDatabase(ctx, projectID, "agent-run", tb.CreateDatabaseOptions{TTLSeconds: &ttl})
	if err != nil {
		return err
	}
	fork, err := c.ForkDatabase(ctx, projectID, db.ID, "agent-fork", tb.ForkDatabaseOptions{TTLSeconds: &forkTTL})
	if err != nil {
		return err
	}
	key, err := c.CreateScopedKey(ctx, tb.ScopedKeyRequest{
		Project: projectID, Database: fork.ID, Access: tb.ScopedKeyReadWrite, TTLSeconds: &forkTTL,
	})
	if err != nil {
		return err
	}

	agent, err := tb.New(tb.Config{APIKey: key.Key})
	if err != nil {
		return err
	}
	if _, err := agent.Database(projectID, fork.ID).Execute(ctx, "CREATE TABLE results (id TEXT PRIMARY KEY, value TEXT)"); err != nil {
		return err
	}

	if _, err := c.SetDatabaseTTL(ctx, projectID, db.ID, 7200); err != nil {
		return err
	}
	if _, err := c.ClearDatabaseTTL(ctx, projectID, db.ID); err != nil {
		return err
	}
	return c.DeleteDatabase(ctx, projectID, db.ID)
}
```

To fork from a point in time, set `PointInTime: &t` in `tb.ForkDatabaseOptions`, where `t` is a `time.Time`.

### Rust

```rust
use terrascale::v1::{
    Client, CreateDatabaseOptions, ForkDatabaseOptions, ScopedKeyAccess, ScopedKeyOptions,
};

async fn run(project: &str, api_key: &str) -> terrascale::v1::Result<()> {
    let client = Client::for_project_api_key(project, api_key).build()?;

    let run = client.create_database("agent_run", &CreateDatabaseOptions {
        ttl_seconds: Some(3600),
    }).await?;
    let fork = client.fork_database(&run.id, "agent_fork", &ForkDatabaseOptions {
        ttl_seconds: Some(600),
        ..Default::default()
    }).await?;
    let scoped = client.create_scoped_key(&ScopedKeyOptions {
        project: Some(project.into()),
        database: fork.id.clone(),
        access: ScopedKeyAccess::ReadWrite,
        ttl_seconds: Some(600),
    }).await?;

    let agent = Client::for_project_api_key(project, scoped.key).build()?;
    agent.database(&fork.id).sql().execute("CREATE TABLE results (id TEXT PRIMARY KEY, value TEXT)", &[]).await?;

    client.set_database_ttl(&run.id, 7200).await?;
    client.clear_database_ttl(&run.id).await?;
    client.delete_database(&run.id).await?;
    Ok(())
}
```

`ForkDatabaseOptions` takes `point_in_time: Some(...)` as an RFC 3339 string for a point-in-time fork.

</div>
