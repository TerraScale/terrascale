---
title: CLI installation and usage
description: Build the TerraScale CLI from source, sign in, manage databases, and run SQL.
---

# CLI installation and usage

The `ts` CLI signs you into TerraScale and runs database commands as your user.
You need a TerraScale account and a project in the [dashboard](https://dash.terrascale.tech).

::: warning Public release pending
The CLI source is merged into [ts-go](https://github.com/TerraScale/ts-go/tree/main/cmd/ts),
but the first public CLI release, hosted installer, and Homebrew tap are still pending.
Use the source build below today. Installation does not establish hosted service availability.
:::

## Build from source today

Install [Go](https://go.dev/doc/install) 1.25 or later and Git. In a terminal:

```sh
git clone https://github.com/TerraScale/ts-go.git
cd ts-go
go build -o ts ./cmd/ts
```

Run `./ts` from this directory, or move the executable to a directory on your `PATH`
to use `ts` as shown below. On Windows, build with `go build -o ts.exe ./cmd/ts`
and run `.\ts.exe` from PowerShell, or add its directory to `PATH`.

Do not install the published SDK `v0.0.1` expecting a CLI: it predates `cmd/ts`.
The source build uses the current `main` branch.

## Release installation (pending)

These commands describe the intended release installation. They are **not available yet**.
Wait for a CLI release on the [releases page](https://github.com/TerraScale/ts-go/releases)
and the installer or tap to be published before using them.

### macOS and Linux

The release target is macOS and Linux on Intel/AMD 64-bit (`amd64`) and ARM64 (`arm64`):

```sh
curl -fsSL https://terrascale.tech/install.sh | sh
```

Homebrew installation is planned through the TerraScale tap:

```sh
brew install terrascale/tap/ts
```

### Windows

Use the source build for now. A native Windows release installer is not currently available.

### Check the installed version

The release CLI is expected to report its version with:

```sh
ts --version
```

The current source CLI does not implement `--version`; it returns an invalid-arguments
error and prints usage instead. This is a pending release feature, not a failed source build.

## Sign in

```sh
ts auth login
```

Open the URL printed by the CLI in your browser and confirm the displayed code.
The command waits until sign-in completes and prints `Signed in.`.
Commands use your project permissions. The CLI stores your sign-in locally and refreshes it when needed.

If you have exactly one project, the CLI selects it automatically. With multiple
projects, put `-project` **before** the command and use a project name or ID:

```sh
ts -project my-project db list
```

Use the project ID if names are ambiguous. Create a project in the dashboard if
the CLI reports `no project`.

## Manage databases

```sh
ts db list
ts db create example
ts db branch example example-preview
```

`db list` prints database names, IDs, and home regions. `db branch` takes the
source database name first and the new branch name second.

::: danger Database deletion
`ts db delete example-preview` deletes that database immediately, without a
confirmation prompt. Check the selected project and database name before running it.
:::

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
[JSON filter queries](/guides/queries).

## Sign out

```sh
ts auth logout
```

This revokes the stored refresh token and removes the local sign-in after a
successful revocation. Run `ts auth login` again if a command reports an expired sign-in.
