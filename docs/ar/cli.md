---
title: CLI installation and usage
description: Install the TerraScale CLI with go install, sign in, and manage databases, collections, documents, SQL and API keys.
---

<!-- i18n:fallback:ar -->
> **الترجمة قيد المراجعة.** لا تتوفر بعد مراجعة بشرية لهذه الصفحة بالعربية، لذلك يظهر المحتوى الأصلي بالإنجليزية.

<div lang="en" dir="ltr">

# CLI installation and usage

The `ts` CLI signs you into TerraScale and manages databases, collections,
documents, read-only SQL and project API keys as your user.
You need a TerraScale account and a project in the [dashboard](https://dash.terrascale.tech).

::: warning Public release pending
The CLI source is merged into [ts-go](https://github.com/TerraScale/ts-go/tree/main/cmd/ts),
but the first public CLI release, hosted installer, and Homebrew tap are still pending.
Install with `go install` or the source build below today. Installation does not establish hosted service availability.
:::

## Install with Go today

Install [Go](https://go.dev/doc/install) 1.25 or later. In a terminal:

```sh
go install terrascale.tech/sdk/cmd/ts@main
export PATH="$(go env GOPATH)/bin:$PATH"
ts help
```

Use `@main`: `@latest` resolves to SDK `v0.0.1`, which predates `cmd/ts` and
fails with `does not contain package terrascale.tech/sdk/cmd/ts`. On Windows,
the same command installs `ts.exe` into `%USERPROFILE%\go\bin`; add that
directory to `PATH`.

## Build from source

Install Go 1.25 or later and Git. In a terminal:

```sh
git clone https://github.com/TerraScale/ts-go.git
cd ts-go
go build -o ts ./cmd/ts
```

Run `./ts` from this directory, or move the executable to a directory on your `PATH`
to use `ts` as shown below. On Windows, build with `go build -o ts.exe ./cmd/ts`
and run `.\ts.exe` from PowerShell, or add its directory to `PATH`.

## Release installation (pending)

These commands describe the intended release installation. They are **not available yet**.
Wait for a CLI release on the [releases page](https://github.com/TerraScale/ts-go/releases)
and the installer or tap to be published before using them.

### macOS and Linux

The release target is macOS and Linux on Intel/AMD 64-bit (`amd64`) and ARM64 (`arm64`):

```sh
curl -fsSL https://terrascale.tech/install.sh | sh
export PATH="$HOME/.local/bin:$PATH"
```

Homebrew installation is planned through the TerraScale tap:

```sh
brew install terrascale/tap/ts
```

### Windows

Use the source build for now. Tagged releases will provide `amd64` and `arm64`
ZIP archives on the [releases page](https://github.com/TerraScale/ts-go/releases).
Verify the archive's SHA-256 against `checksums.txt` before extracting `ts.exe`
into a user-owned directory on `PATH`.

### Check the installed version

The release CLI is expected to report its version with:

```sh
ts --version
```

Release builds print `ts 0.0.X`. `go install` and source builds print `ts dev`.
`ts help` (or `ts --help`) lists every command. Invalid arguments exit with
status 2 and other failures with status 1.

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

Use the project ID from `ts projects` if names are ambiguous. Create a project
in the dashboard if the CLI reports `no project`.

## Manage databases

```sh
ts db list
ts db create example
ts db branch example example-preview
```

`db list` prints database names, IDs, and home regions, and an empty table
before the project's first database. `db branch` takes the
source database name first and the new branch name second.

::: danger Database deletion
`ts db delete example-preview` deletes that database immediately, without a
confirmation prompt. Check the selected project and database name before running it.
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
exports and bulk imports see [Export your data](/ar/guides/export) and
[Import an existing database](/ar/guides/import).

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
[JSON filter queries](/ar/guides/queries).

## API keys

```sh
ts keys create backend example   # optional database restricts the key
ts keys list
ts keys revoke 01JZ8Q5B7C9D1E3F5G7H9J1K3M
```

`keys create` makes a project key with `databases:read`, `databases:write`,
`documents:read` and `documents:write`. It prints the key once on standard
output; store it immediately in your secret manager. Use the key with an SDK's
API key token exchange, as the [quickstarts](/ar/getting-started) show.

## Sign out

```sh
ts auth logout
```

This revokes the stored refresh token and removes the local sign-in after a
successful revocation. Run `ts auth login` again if a command reports an expired sign-in.

</div>
