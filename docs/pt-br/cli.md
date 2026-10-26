---
title: CLI installation and usage
description: Install the TerraScale CLI in one line, sign in, and manage projects, databases, collections, documents, indexes, imports, SQL and API keys.
---

<!-- i18n:fallback:pt-br -->
> **Tradução pendente.** Esta página ainda não passou por revisão humana em português e mostra o conteúdo original em inglês.

<div lang="en" dir="ltr">

# CLI installation and usage

The `ts` CLI signs you into TerraScale and manages databases, collections,
documents, read-only SQL and project API keys as your user.
You need a [TerraScale account](https://dash.terrascale.tech).

See the [automation and JSON reference](/pt-br/cli/automation) for machine output contracts.

## Install

macOS and Linux, Intel/AMD 64-bit (`amd64`) and ARM64 (`arm64`):

```sh
curl -fsSL https://terrascale.tech/install.sh | sh
export PATH="$HOME/.local/bin:$PATH"
ts --version
```

The installer downloads the latest `v0.0.X` release from the
[releases page](https://github.com/TerraScale/ts-go/releases), checks its
SHA-256 against `checksums.txt`, and installs `ts` to `~/.local/bin` without
sudo. `sh install.sh 0.0.2` installs a specific release.

With Homebrew on macOS or Linux:

```sh
brew install terrascale/tap/ts
```

On Windows, download `ts_windows_amd64.zip` or `ts_windows_arm64.zip` from the
[releases page](https://github.com/TerraScale/ts-go/releases), verify it against
`checksums.txt` (`Get-FileHash .\ts_windows_amd64.zip -Algorithm SHA256`), and
extract `ts.exe` into a user-owned directory on `PATH`.

With Go 1.25 or later on any platform:

```sh
go install terrascale.tech/sdk/cmd/ts@latest
```

Release builds print `ts 0.0.X`; `go install` and source builds print `ts dev`.
`ts help` (or `ts --help`) lists every command. Invalid arguments exit with
status 2 and other failures with status 1.

### Upgrade

Use the same installation method to upgrade, then check the installed binary:

```sh
# Installer: rerun it to replace the binary with the latest release.
curl -fsSL https://terrascale.tech/install.sh | sh
# Homebrew:
brew update && brew upgrade ts
# Go:
go install terrascale.tech/sdk/cmd/ts@latest
# After your chosen method:
ts --version
```

### Shell completion

Run the block for your shell. All files go into user-owned directories.

```bash
mkdir -p ~/.local/share/bash-completion/completions
ts completion bash > ~/.local/share/bash-completion/completions/ts
source ~/.local/share/bash-completion/completions/ts
```

```zsh
mkdir -p ~/.local/share/terrascale
ts completion zsh > ~/.local/share/terrascale/completion.zsh
source ~/.local/share/terrascale/completion.zsh
```

Add the zsh `source` line to `~/.zshrc` to load it in new sessions.

```fish
mkdir -p ~/.config/fish/completions
ts completion fish > ~/.config/fish/completions/ts.fish
source ~/.config/fish/completions/ts.fish
```

## Start from the CLI

After installing, create your own project and database:

```sh
ts auth login
ts projects create my-project
ts -project my-project db create example
```

## Sign in

```sh
ts auth login
```

Open the URL printed by the CLI in your browser and confirm the displayed code.
The command waits until sign-in completes and prints `Signed in.`.
Commands use your project permissions. The CLI stores your sign-in in your
user config directory (`~/.config/terrascale/cli.json` on Linux,
`~/Library/Application Support/terrascale/cli.json` on macOS,
`%AppData%\terrascale\cli.json` on Windows), readable only by you (mode 0600),
and refreshes it when needed.

```sh
ts whoami     # the signed-in account
ts projects   # every project you can reach, with its ID and your role
```

If you have exactly one project, the CLI selects it automatically. With multiple
projects, put `-project` **before** the command and use a project name or ID:

```sh
ts -project my-project db list
```

Use the project ID from `ts projects` if names are ambiguous. If the CLI
reports `no project`, create one:

```sh
ts projects create my-project
ts -org my-org projects create other-project   # pick the organization
```

`projects create` uses your only organization, or creates an organization of
the same name when you have none.

In CI, set `TERRASCALE_API_KEY` to an API key instead of running `ts auth login`.
Database commands then act as that key in its project. Store the key in your CI
secret store. See [CI](/pt-br/guides/migrations#ci).

## Manage databases

```sh
ts db list
ts db create example
ts db branch example example-preview
```

Database deletion asks you to type the database name in a terminal before it
changes placement or deletes data. For an unattended script, explicitly consent
with `ts -project my-project -yes db delete example`. Keep `-yes` before the command.


`db list` prints database names, IDs, home regions and replica regions, and an
empty table before the project's first database. `db info` prints one
database's metadata as JSON.

## Regions and backups

```sh
ts db regions example sjc fra   # replicate to sjc and fra besides the home region
ts db regions example none      # remove every replica
ts backup create example
ts backup list example
ts backup restore example 01JZ8Q7D9F1G3H5J7K9M1N3P5Q example-restored
```

`db regions` replaces the replica list; the home region never changes. Serving
regions are `gru`, `sjc` and `fra`. `backup restore` creates a new, independent
database from the backup and leaves the source untouched. See
[Backups and restore](/pt-br/guides/backups-and-restore) and
[Replication and durability](/pt-br/concepts/replication). `db branch` takes the
source database name first and the new branch name second.

::: warning Home-only databases are read-only
`ts db regions example none` removes every replica. Writes to the resulting home-only database fail with CLI exit status 1 and HTTP `409 placement_needs_two_regions`. Add replica regions again with `ts db regions example sjc fra` to resume writes.
:::

Branching and restoring create an additional database and require spare database
capacity. The Free plan's quota is one database; when it is full,
`db branch`, `backup restore` and `backup restore-at` fail with HTTP
`409 database_limit`. Increase your plan's database allowance before making a copy.

```sh
ts backup restore-at example 2026-10-23T12:00:00Z example-at-noon
```

`backup restore-at` restores the database as it was at an RFC 3339 time into a
new database. The time must be inside the database's restore window; a time
continuous backup has not yet covered fails with `backup_history_incomplete`.

::: danger Database deletion
`ts db delete example-preview` asks you to type the database name before deleting
it. Check the selected project first. For automation, explicitly confirm with
`ts -yes db delete example-preview`; non-interactive deletion otherwise fails
without changing the database.
:::

## Collections and documents

```sh
ts collection create example customers
ts collection list example
ts doc put example customers '{"name":"Ana","plan":"pro"}'
ts doc put example customers 01JZ8Q3Y6W2V4T0R9P7N5M3K1H '{"name":"Bo"}'
echo '{"name":"Cy"}' | ts doc put example customers -
ts doc get example customers 01JZ8Q3Y6W2V4T0R9P7N5M3K1H
ts doc list example customers
ts doc delete example customers 01JZ8Q3Y6W2V4T0R9P7N5M3K1H
ts collection drop example customers
```

`doc put` without an ID creates a document with a new ID and prints it; with
an ID it creates or replaces that document. Document IDs are canonical
uppercase ULIDs. Pass `-` instead of the JSON to read the document from
standard input. `doc get` prints the document with its ID and version.

`doc list` prints every document of the collection as one JSON line each.
`doc import` puts those lines back, creating the collection if needed, so the
two copy a collection between databases or projects:

```sh
ts doc list example customers > customers.jsonl
ts -project other-project doc import example customers < customers.jsonl
```

This is a client-side copy, one request per document. For whole-database
exports and bulk imports see [Export your data](/pt-br/guides/export) and
[Import an existing database](/pt-br/guides/import).

## Indexes, schemas and TTL

```sh
ts index create example customers plan region   # 1 to 4 dotted member paths
ts index list example [customers]
ts index drop example customers_plan_region
ts schema set example customers '{"type":"object","required":["name"]}'
ts schema get example customers
ts schema delete example customers
ts ttl set example sessions /expiresAt
ts ttl get example sessions
ts ttl delete example sessions
```

`index create` builds the index before it returns and prints its name, derived
from the collection and fields. `schema set` declares a JSON Schema (2020-12)
that writes must satisfy; pass `-` to read it from standard input. `ttl set`
takes a JSON Pointer: documents whose member there holds a past RFC 3339 UTC
time are deleted by an hourly sweep. See [Indexes](/pt-br/guides/indexes) and [TTL](/pt-br/guides/ttl).

## Import a database file

```sh
ts import legacy sqlite ./app.db
ts import legacy postgres_plain ./dump.sql
ts import status legacy
ts import cancel legacy
```

`import` creates a new database, uploads the file (up to 1 GiB), prints the
preview of each source table and its target collection, confirms it, and waits
until the copy and validation finish. It refuses a preview with blocking
issues; `import status` prints the whole job as JSON. Running the same command
again resumes the same import. See [Import an existing database](/pt-br/guides/import).

### Plain document NDJSON

After upgrading the CLI, import plain JSON objects (one per line) with the
collection flag **before** the command:

```sh
ts -collection customers import customers-copy ndjson ./customers.ndjson
```

TerraBase [exports](/pt-br/guides/export) contain `{collection,id,document}` envelopes.
Keep them intact and omit `-collection`: `ts import restored ndjson ./export.ndjson`.

## Watch changes

```sh
ts db changes example                 # every collection
ts db changes example customers orders
```

`db changes` prints each committed change from now on as one JSON line with
the collection, ID, operation, version and document, until you press Ctrl-C.
Delivery is at least once. See [Realtime changes](/pt-br/guides/realtime).

## Usage

```sh
ts usage           # the project and each database, as JSON
ts usage example   # one database
```

`usage` prints storage, document counts and hourly reads and writes for the
last day.

## Run SQL

Pass the database name and the entire query as two arguments; quote the query
so your shell keeps it together:

```sh
ts sql example 'SELECT * FROM customers'
ts -project my-project sql example 'SELECT * FROM customers'
```

Replace `customers` with a collection available in your database. This endpoint
supports a read-only `SELECT` subset, not arbitrary SQL or mutations. Results
default to 100 rows, with a maximum limit of 1,000 and an 8 MiB response cap.
The CLI sends the query to the database SQL endpoint and prints each returned row as JSON on its
own line. A truncated result produces a warning on standard error; it is not
an export of all matching rows. SQL is separate from the Documents API's
[JSON filter queries](/pt-br/guides/queries).

## Schema migrations

```sh
ts migrate new "add orders"    # add the next migrations/NNNN_<name>.sql
ts migrate up app              # apply pending migrations, each in one transaction
ts migrate status app          # list each migration and whether it is applied
ts migrate diff app-orders     # print the schema changes against the branched-from database
ts exec app 'SELECT * FROM orders LIMIT 10'  # run one relational SQL statement
```

See [Schema migrations](/pt-br/guides/migrations) for the file format, history and workflow.

## API keys

```sh
ts keys create backend example   # optional database restricts the key
ts keys list
ts keys revoke 01JZ8Q5B7C9D1E3F5G7H9J1K3M
```

`keys create` makes a project key with `databases:read`, `databases:write`,
`documents:read` and `documents:write`. It prints the key once on standard
output; store it immediately in your secret manager. Use the key with an SDK's
API key token exchange, as the [quickstarts](/pt-br/getting-started) show.

### Key lifetime and allowed networks

Keys default to no expiry and read/write scopes. Prefer an expiry and a database
restriction. Put key-creation flags before the command:

```sh
ts -expires 30d -allow-cidr 203.0.113.0/24 keys create app example
```

Replace the documentation network above with your application's real egress CIDR.
Expiry accepts `30d`, `90d`, `365d` or `never`; CIDRs are comma-separated. Treat the
one-time key output as a secret; write it only to a private file or secret store.

## Sign out

```sh
ts auth logout
```

This revokes the stored refresh token and removes the local sign-in after a
successful revocation. Run `ts auth login` again if a command reports an expired sign-in.

</div>
