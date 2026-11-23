---
name: terrascale-getting-started
description: Set up TerraScale from an empty project through a working SQL application. Use when getting started with TerraScale, installing the ts CLI, creating a database, configuring API keys, running a first query, or connecting an AI agent with MCP.
---

# Get started with TerraScale

Use this skill for the first working application connection.
See also: terrascale-sdks for other languages and deeper SDK usage.

## Prerequisites

- [ ] An account at [the dashboard](https://dash.terrascale.tech/register).
- [ ] A terminal; Bash, curl and jq for the HTTP example.
- [ ] Node.js 22 or 24 and a TerraScale checkout for the JavaScript example.
- [ ] An application secret store; exclude local .env files from Git.

Sources: [getting started](https://docs.terrascale.tech/getting-started/),
[JavaScript](https://docs.terrascale.tech/getting-started/javascript).
Never print credentials, .env contents, tokens or secret connection URLs.

## 1. Inventory and plan

- [ ] Identify the organization, project and database before creating resources.
- [ ] Decide whether to create a database or connect to an existing one.
- [ ] Choose the CLI for setup, an SDK for the app, and HTTP for a direct probe.

TerraScale SQL uses the **SQLite dialect**. Postgres drivers are a transport
option; they do not provide Postgres-only syntax or unrestricted catalog access.
Sources: [SQL](https://docs.terrascale.tech/concepts/sql),
[Postgres clients](https://docs.terrascale.tech/guides/postgres-clients).

## 2. Install the CLI and sign in

Choose one documented installation method.

Homebrew on macOS or Linux:

~~~sh
brew install terrascale/tap/ts
~~~

Installer on macOS or Linux, amd64 or arm64; it verifies the release SHA-256
and installs without sudo:

~~~sh
curl -fsSL https://terrascale.tech/install.sh | sh
export PATH="$HOME/.local/bin:$PATH"
ts --version
~~~

With Go 1.25 or newer:

~~~sh
go install terrascale.tech/sdk/cmd/ts@latest
~~~

On Windows, download ts_windows_amd64.zip or ts_windows_arm64.zip from
[releases](https://github.com/TerraScale/terrascale/releases), verify against
checksums.txt, and extract ts.exe into a user-owned directory on PATH:

~~~powershell
Get-FileHash .\ts_windows_amd64.zip -Algorithm SHA256
~~~

Upgrade using the same installation method. Go/source installs print ts dev;
release builds print the version. Source: [CLI](https://docs.terrascale.tech/cli).

~~~sh
ts auth login
ts whoami
ts projects
~~~

Complete the browser sign-in. With multiple projects, put -project before the
command and use its ID if names are ambiguous.
Source: [CLI access](https://docs.terrascale.tech/cli/access).

## 3. Create or select resources and credentials

Skip creation for resources that already exist:

~~~sh
ts projects create my-project
ts -project my-project db create app
ts -project my-project db info app
~~~

Project creation uses your only organization, or creates one with the same name
when you have none. Use -org before the command to choose an organization.
Source: [CLI](https://docs.terrascale.tech/cli).

In the application's directory, initialize its connection:

~~~sh
ts -project my-project init app
~~~

Init reuses or creates the database, creates a database-scoped SQL read/write key,
and writes a mode-0600 .env without overwriting an existing file. It detects the
language; -lang js selects JavaScript. Keep its one-time secret output private.
Generated variables are TERRASCALE_API_KEY, TERRASCALE_PROJECT_ID,
TERRASCALE_DATABASE_NAME and TERRASCALE_REGION.
Source: [CLI init](https://docs.terrascale.tech/cli).

Alternatively, open **API keys** in the dashboard, restrict a key to the database,
grant sql:read and sql:write, and put it in the application secret manager.
The **Organization** page shows **Current project** ID. For manual CLI creation,
ts keys create backend <database ULID> takes the immutable id from db info,
not the database name; capture the one-time output privately.
Sources: [dashboard](https://docs.terrascale.tech/guides/dashboard),
[CLI keys](https://docs.terrascale.tech/cli).

Zero configuration means features and ready regions need no activation flags or
operator tuning. Applications still need authorized credentials, and selected
placement still bounds residency.
Source: [zero configuration](https://docs.terrascale.tech/guides/zero-configuration).

## 4. Run a first query on three surfaces

### CLI

~~~sh
ts -project my-project sql app "CREATE TABLE people (id TEXT PRIMARY KEY, name TEXT NOT NULL)"
ts -project my-project sql app "INSERT INTO people (id, name) VALUES ('a1', 'Ada')"
ts -project my-project sql app "SELECT id, name FROM people WHERE id = 'a1'"
~~~

Expect a JSON row containing a1 and Ada. Each statement commits independently.
Source: [SQL quickstart](https://docs.terrascale.tech/concepts/sql#quickstart).

### SQL HTTP API

Supply TERRASCALE_API_KEY, TERRASCALE_PROJECT_ID and TERRASCALE_DATABASE_NAME
through the environment. With tracing off, exchange the API key:

~~~bash
set +x
set -o pipefail
ts_token() {
  key="${TERRASCALE_API_KEY#sk_}"
  printf 'user = "%s:%s"\n' "${key%%_*}" "$TERRASCALE_API_KEY" |
    curl -sS --fail-with-body --config - -d grant_type=client_credentials \
      https://auth.terrascale.tech/terrascale/oauth/token |
    jq -er '.access_token | select(type == "string" and length > 0)'
}
export TERRASCALE_ACCESS_TOKEN="$(ts_token)"
curl -sS --fail-with-body --header @/dev/fd/3 \
  -H 'Content-Type: application/json' \
  -d '{"statements":[{"sql":"SELECT id, name FROM people WHERE id = ?","args":["a1"]}]}' \
  "https://api.terrascale.tech/v1/projects/$TERRASCALE_PROJECT_ID/databases/$TERRASCALE_DATABASE_NAME/sql/execute" \
  3< <(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN")
~~~

Expect results[0].rows containing ["a1", "Ada"]. The API key is a client
credential, not a bearer token. Tokens last 60 seconds: call ts_token again
before later requests. One HTTP request runs all statements in one transaction.
Sources: [token exchange](https://docs.terrascale.tech/getting-started/#exchange-the-key-for-an-access-token),
[SQL HTTP](https://docs.terrascale.tech/guides/sql-http).

### JavaScript SDK

The quickstart describes source 0.0.19; published 0.0.17 has the previous API.
Use the actual source checkout path for this example:

~~~sh
npm init -y
npm install /path/to/terrascale/sdk/js
~~~

Save as first-query.mjs with the three application variables exported:

~~~js
import { createTerraBaseV1Client } from "@terrascale/sdk";

const client = createTerraBaseV1Client({
  baseUrl: "https://api.terrascale.tech",
  project: process.env.TERRASCALE_PROJECT_ID,
  apiKey: process.env.TERRASCALE_API_KEY,
});
const db = client.database(process.env.TERRASCALE_DATABASE_NAME);
const people = await db.query("SELECT id, name FROM people WHERE id = ?", ["a1"]);
console.log(people);
~~~

~~~sh
node first-query.mjs
~~~

Expect the same row. The client exchanges and refreshes the key automatically.
Keep privileged keys on the server; bind values as parameters.
Source: [JavaScript quickstart](https://docs.terrascale.tech/getting-started/javascript).
See also: terrascale-sdks.

## 5. Verify placement, usage and agent access

- [ ] Review database metadata and key restrictions in the dashboard.
- [ ] Choose regions for latency and residency, measuring from the application.
- [ ] Check allowance before adding a branch or restore database.

Current regions are São Paulo (gru), San Francisco Bay Area (sjc) and Frankfurt
(fra). A database uses at least one; TerraScale selects the region ordering writes.
For a separate example with explicit placement:

~~~sh
ts -project my-project db create regional-app -regions gru,fra
ts -project my-project db regions app
ts -project my-project usage app
~~~

REST reads are eventually consistent. Postgres and WebSocket sessions preserve
read-your-writes. Copies in other database regions are not billed separately.
Sources: [regions](https://docs.terrascale.tech/concepts/regions),
[replication](https://docs.terrascale.tech/concepts/replication),
[CLI usage](https://docs.terrascale.tech/cli).

Free has one live database, 0.1 GB storage and 50,000 monthly operation units;
a source plus branch needs two database slots. Starter starts at $3/month.
One unit is each started 4 KiB written, at least one per changed row. SQL reads
are unbilled but have fair use. Paid plans have overages and hard operation caps;
customer-set hard dollar caps are coming. Recheck the current tables for estimates.
Sources: [pricing](https://docs.terrascale.tech/pricing),
[limits](https://docs.terrascale.tech/limits).

Requests allow 1–256 statements, 16 KiB per statement, a 1 MiB body, and results
of 10,000 rows / 8 MiB. Transactions change at most 1,024 rows or grow files by
8 MiB. Interactive transactions last at most five seconds; plan query timeouts
can be shorter. Source: [limits](https://docs.terrascale.tech/limits).

For an agent, add the HTTP MCP server and sign in:

~~~sh
codex mcp add terrascale --url https://mcp.terrascale.tech/
codex mcp login terrascale
~~~

Call get_context first and discover tools before using them. OAuth acts as the
dashboard user; scope selectors do not narrow that user's authority. Clients
without MCP sign-in can use a personal access token in protected credential storage.
Source: [MCP](https://docs.terrascale.tech/guides/mcp).

## 6. Troubleshoot and hand off

| Symptom | Check and next action | Source |
| --- | --- | --- |
| ts missing | Add the installation directory to PATH; check ts --version. | [CLI](https://docs.terrascale.tech/cli) |
| No project or ambiguous name | Run ts projects; use -project ID before the command. | [CLI](https://docs.terrascale.tech/cli) |
| HTTP 401 | Exchange the key instead of sending it as bearer; refresh the token. | [Getting started](https://docs.terrascale.tech/getting-started/) |
| forbidden_scope | Check project, database restriction and SQL scopes. | [SQL HTTP](https://docs.terrascale.tech/guides/sql-http) |
| Missing JS export | Use source for this example; 0.0.17 has the previous API. | [JS quickstart](https://docs.terrascale.tech/getting-started/javascript) |
| database_limit | Branches/restores need a free slot; review the plan. | [Limits](https://docs.terrascale.tech/limits) |
| tenant_busy | Honor Retry-After and reduce concurrency. | [Limits](https://docs.terrascale.tech/limits) |
| quota_exceeded | Check write cap, read fair use or storage before retrying. | [Limits](https://docs.terrascale.tech/limits) |
| Query too expensive | Index, narrow and paginate the query. | [Limits](https://docs.terrascale.tech/limits) |
| Stale REST read | Check routing and consistency before concluding data was lost. | [Replication](https://docs.terrascale.tech/concepts/replication) |

- [ ] Record resource IDs, installation method and successful query result.
- [ ] Put deployment credentials in its secret manager.
- [ ] To roll back setup, stop using the new connection and revoke its key.
- [ ] Preserve needed data before deleting a database: deletion destroys backup
  data keys; independent forks survive until deleted or expired.

Sources: [CLI keys](https://docs.terrascale.tech/cli),
[recovery and deletion](https://docs.terrascale.tech/concepts/replication).
