---
title: CLI installation and usage
description: Install the TerraScale CLI in one line, sign in, and manage projects, databases, backups, SQL and API keys.
---

<!-- i18n:fallback:ru -->
> **Перевод на проверке.** У этой страницы пока нет проверенного человеком перевода на русский, поэтому показан английский оригинал.

<div lang="en" dir="ltr">

# CLI installation and usage

The `ts` CLI signs you into TerraScale and manages projects, databases,
backups, SQL and project API keys as your user.
You need a [TerraScale account](https://dash.terrascale.tech).

See the [automation and JSON reference](/ru/cli/automation) for machine output contracts.

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
secret store. See [CI](/ru/guides/migrations#ci).

## Manage databases

```sh
ts db list
ts db create example
ts db branch example example-preview
```

Database deletion asks you to type the database name in a terminal before it
changes placement or deletes data. For an unattended script, explicitly consent
with `ts -project my-project -yes db delete example`. Keep `-yes` before the command.


`db list` lists databases. `db info` prints one database's metadata as JSON.

## Regions and backups

SQL placement and backup guidance is being updated. See [SQL](/ru/concepts/sql).

## Usage

```sh
ts usage           # the project and each database, as JSON
ts usage example   # one database
```

`usage` prints storage and hourly reads and writes for the last day.

## Run SQL

SQL is described in [SQL](/ru/concepts/sql). The CLI command for running it is not documented here yet.

## Schema migrations

```sh
ts migrate new "add orders"    # add the next migrations/NNNN_<name>.sql
ts migrate up app              # apply pending migrations, each in one transaction
ts migrate status app          # list each migration and whether it is applied
ts migrate diff app-orders     # print the schema changes against the branched-from database
ts exec app 'SELECT * FROM orders LIMIT 10'  # run one relational SQL statement
```

See [Schema migrations](/ru/guides/migrations) for the file format, history and workflow.

## API keys

```sh
ts keys create backend example   # optional database restricts the key
ts keys list
ts keys revoke 01JZ8Q5B7C9D1E3F5G7H9J1K3M
```

`keys create` makes a project key with read and write access. It prints the key
once on standard output; store it immediately in your secret manager. Use the
key with an SDK's API key token exchange, as [getting started](/ru/getting-started/)
shows.

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
