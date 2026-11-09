---
title: CLI installation and usage
description: Install the TerraScale CLI in one line, sign in, and manage projects, databases, backups, SQL and API keys.
---

<!-- i18n:fallback:ar -->
> **الترجمة قيد المراجعة.** لا تتوفر بعد مراجعة بشرية لهذه الصفحة بالعربية، لذلك يظهر المحتوى الأصلي بالإنجليزية.

<div lang="en" dir="ltr">

# CLI installation and usage

The `ts` CLI signs you into TerraScale and manages projects, databases,
backups, SQL and project API keys as your user.
You need a [TerraScale account](https://dash.terrascale.tech).

See the [automation and JSON reference](/ar/cli/automation) for machine output contracts.

## Install

macOS and Linux, Intel/AMD 64-bit (`amd64`) and ARM64 (`arm64`):

```sh
curl -fsSL https://terrascale.tech/install.sh | sh
export PATH="$HOME/.local/bin:$PATH"
ts --version
```

The installer downloads the latest `sdk/go/v0.0.X` release from the
[releases page](https://github.com/TerraScale/terrascale/releases), checks its
SHA-256 against `checksums.txt`, and installs `ts` to `~/.local/bin` without
sudo. `sh install.sh 0.0.2` installs a specific release.

With Homebrew on macOS or Linux:

```sh
brew install terrascale/tap/ts
```

On Windows, download `ts_windows_amd64.zip` or `ts_windows_arm64.zip` from the
[releases page](https://github.com/TerraScale/terrascale/releases), verify it against
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
secret store. See [CI](/ar/guides/migrations).

## Manage databases

```sh
ts db list
ts db create example
ts db info example
```

`db list` lists databases. `db info` prints one database's metadata as JSON,
including its immutable ULID. `db export <db>` downloads a database as a SQL
dump to stdout; `-o file` writes it atomically, leaving no file if the export
is interrupted, and `-force` overwrites an existing file. `db rename <db> <new-name>`
(the long form is `ts databases rename`) renames a database by name or ULID:

```sh
ts db rename example example-prod
```

### Branches

Two forms exist. `db branch <source> <db>` copies an existing database into a
new one and keeps the copy forever:

```sh
ts db branch example example-preview
```

`db branch create <name> --from <db> [--ttl 7d]` creates a short-lived branch
that expires on its own (default lifetime 7 days):

```sh
ts db branch create preview --from example --ttl 1d
ts db branch list
ts db branch delete preview
```

`db branch url <name>` is the easiest way to get a Postgres connection URL: it
issues a scoped secret URL for that one database, valid for at most 7 days.
It requires an API key, so run it with `TERRASCALE_API_KEY` set:

```sh
TERRASCALE_API_KEY=... ts db branch url preview
```

### Placement

```sh
ts -all-regions db create example
ts db placement example -replica-regions gru,iad
ts db move-home example iad
ts db regions example
```

`db create` places the home region automatically; `-home-region <region>`,
`-replica-regions <a,b>` or `-all-regions` request specific placement.
`db placement` changes a database's replica regions later,
`db move-home <db> <region>` moves the home to one of its replica regions, and
`db regions <db>` prints the home region and each replica's pull progress.

Database deletion asks you to type the database name in a terminal before it
deletes data. For an unattended script, explicitly consent
with `ts -project my-project -yes db delete example`. Keep `-yes` before the command.

## Backups

```sh
ts backup list example
ts backup create example
```

`backup list` shows each backup's ID, status, trigger, size and creation time;
`backup create` takes a backup on demand.

### Policy

```sh
ts backup policy example
ts backup policy-set example "0 3 * * *" 14
```

`backup policy` reads the snapshot schedule and retention.
`backup policy-set <db> <cron|none> <days>` sets a five-field UTC cron
schedule and a retention of 1 to 35 days; `none` stops scheduled backups.

### Destination

```sh
ts backup destination get
cat credentials.json | ts -yes backup destination set
ts backup destination delete
```

The project backup bucket is where customer-owned backups are shipped.
`set` reads the credential JSON from stdin and requires `-yes`; `delete`
types the project ID before removing the destination.

### Restore

```sh
ts backup window example
ts backup plan example 2025-01-01T00:00:00Z
ts backup restore example 01JZ8Q5B7C9D1E3F5G7H9J1K3M example-restored
ts backup restore-at example 2025-01-01T00:00:00Z example-restored
ts -yes backup rewind example 2025-01-01T00:00:00Z
```

`backup window` shows the proven restore interval for the platform (`platform`)
or your own bucket (`customer`). `backup plan <db> <time> [platform|customer]`
previews the restore metadata for an RFC3339 instant before restoring.
`backup restore <db> <backup id> <new db>` restores one backup into a new
database. `backup restore-at <db> <time> <new db> [platform|customer]` restores
the database state at an RFC3339 instant into a new database (default
platform). `backup rewind <db> <time> [platform|customer]` restores the
database **in place** to an instant; later changes are lost, so it requires
`-yes` for automation.

## Organizations, projects and context

```sh
ts orgs
ts orgs create acme
ts orgs info acme
ts orgs rename acme acme-corp
ts orgs delete acme
```

`orgs` lists every accessible organization. `orgs create` makes a new one;
`orgs info` inspects one; `orgs rename` needs admin and `orgs delete` needs
owner and an empty organization.

```sh
ts projects info my-project
ts projects rename my-project prod
ts -yes projects delete 01JD2V5QANWG7Z4KQVKJFM8RHT
```

`projects info` inspects a project (add `-org <name>` to restrict lookup);
`projects rename` needs admin; `projects delete` removes an identity project
after all its databases are gone and requires typing the project ID or an
explicit `-yes`.

```sh
ts context show
ts context set acme prod example
ts context clear
```

`context` saves verified organization, project and database selectors so you
can stop repeating `-project`; `context set` takes `<org> <project>
[<database>]`, and other commands accept `@` as the saved database.
`ts billing` prints the dashboard billing URL without signing in.

## Usage

```sh
ts usage           # the project and each database, as JSON
ts usage example   # one database
```

`usage` prints storage and hourly reads and writes for the last day.

## Run SQL

SQL is described in [SQL](/ar/concepts/sql). Run one statement with `ts sql <db> <statement>`; rows print as JSON lines, writes print `N rows affected`:

```sh
ts sql example "CREATE TABLE people (id TEXT PRIMARY KEY, name TEXT)"
ts sql example "INSERT INTO people (id, name) VALUES ('a1', 'ada'), ('a2', 'bob')"
ts sql example "SELECT id, name FROM people ORDER BY id"
```

With several projects, put `-project <name>` before `sql`. For a multiline SQL shell with session history, use `ts shell <db>` (`.run` submits; each engine call runs exactly one statement).

## Functions

```sh
ts functions deploy example handler.js
ts functions list example
ts functions invoke example handler -data '{"id":"a1"}'
ts functions logs example handler
ts functions delete example handler
```

`functions deploy` deploys or replaces a JavaScript function from a file or a
directory (a directory deploys its `index.js`); `-name <name>` overrides the
name and `-cron <expr>` schedules it. The file is a plain script, not an ES
module, that defines `function handler(event)`. `functions invoke` runs one
function (`-data <json>` sends a body, `-` reads stdin), `functions logs`
prints recent invocations as JSON lines newest first (`-limit 1..100`), and
`functions delete` removes a function and its logs.

## Schema migrations

```sh
ts migrate new "add orders"    # add the next migrations/NNNN_<name>.sql
ts migrate up app              # apply pending migrations, each in one transaction
ts migrate status app          # list each migration and whether it is applied
ts migrate diff app-orders     # print the schema changes against the branched-from database
ts sql app 'SELECT * FROM orders LIMIT 10'  # run one SQL statement
```

See [Schema migrations](/ar/guides/migrations) for the file format, history and workflow.

## Schema files

```sh
ts --database example schema pull
ts --database example schema diff schema.sql
ts --database example schema apply schema.sql
```

`schema pull` writes `schema.sql` from the database, `schema diff` prints the
DDL needed to reach the file, and `schema apply` applies it atomically;
`--dry-run` previews and `--allow-drop` consents to DROP operations.

## API keys

```sh
ts db info example   # the "id" field is the database's canonical ULID
ts keys create backend 01JD2V5QANWG7Z4KQVKJFM8RHT
ts keys list
ts keys rotate 01JZ8Q5B7C9D1E3F5G7H9J1K3M 1h
ts keys revoke 01JZ8Q5B7C9D1E3F5G7H9J1K3M
```

`keys create` takes the database's immutable ULID (from `ts db info <name>`),
not its name. It makes a project key with read and write access, restricted to
that database. It prints the key
once on standard output; store it immediately in your secret manager. Use the
key with an SDK's API key token exchange, as [getting started](/ar/getting-started/)
shows. `keys rotate <id> <grace>` rotates a key once; the grace is a required
whole-second duration from `0s` to `24h` during which the previous secret
stays valid, and the new secret appears once on stdout.

Creating or rotating a key needs a fresh sign-in confirmation with your password
or an email code, plus two-factor authentication when enrolled. The command
prints the hosted confirmation URL and waits for you to approve it in a browser;
after approval the one-time secret is printed in the terminal. Ctrl+C cancels
the wait; an unconfirmed request expires after five minutes and the command
exits with `403 step_up_required`. `keys list` and `keys revoke` work from the
CLI without confirmation.

### Key lifetime and allowed networks

Keys default to no expiry and read/write scopes. Prefer an expiry and a database
restriction. Put key-creation flags before the command:

```sh
ts -expires 30d -allow-cidr 203.0.113.0/24 keys create app 01JD2V5QANWG7Z4KQVKJFM8RHT
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
