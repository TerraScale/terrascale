---
title: Branches
---

<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

<div lang="en" dir="ltr">

# Branches

A branch is a new database in the same project, created by copying a full, consistent snapshot of the source, including real customer data. Copy time and storage grow with the data; later writes are independent. Check the returned `homeRegion` and `replicaRegions` before use: branching uses default placement, which can differ from the source. The published `@terrascale/sdk@0.0.9` supports `client.databases.branch(sourceName, { name: "orders-migration-test" })`; the examples below use HTTP.

## Before you start

You need a project access token for this project with `databases:write`. A missing or invalid token answers `401`, a missing scope `403`, and another project or a database outside the token's narrowing `404`. See [authentication](/hi/guides/authentication).

```bash
export TERRASCALE_API_URL='https://api.terrascale.tech'
export PROJECT='YOUR_PROJECT_ID' DATABASE='orders' REGION=
# TERRASCALE_ACCESS_TOKEN comes from your secret manager, never a command-line argument.
headers() {
  printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN"
  [ -z "$REGION" ] || printf 'X-TerraScale-Region-Pin: %s\n' "$REGION"
}
api() { curl -sS --fail-with-body -H @<(headers) "$@"; }
```

`$DATABASE` is the name of the source database.

## Create a branch

```bash
api -X POST -H 'Content-Type: application/json' \
  -d '{"name":"orders-migration-test"}' \
  "$TERRASCALE_API_URL/v1/projects/$PROJECT/databases/$DATABASE/branches"
```

`name` is required. It starts with a lowercase letter, then lowercase letters, digits, `_` or `-`, at most 63 characters, and must be free in the project. Reserved device names such as `con`, `nul`, `com0`–`com9` and `lpt0`–`lpt9` are rejected.

The call answers `201` with `Location: /v1/projects/$PROJECT/databases/orders-migration-test`:

```json
{
  "id": "01JB7Q9X5R3T1V7W9Y3Z5A7C0E",
  "projectId": "01JB7PZ4M6N8P0Q2R4S6T8V0W2",
  "name": "orders-migration-test",
  "branchedFrom": "01JB7Q2C8D4F6G0H2J4K6M8N0P",
  "createdAt": "2026-10-22T14:35:12+00:00"
}
```

`branchedFrom` is the source database's id. The body also has `region` (the region that answered), `homeRegion` and `replicaRegions`. The branch is an ordinary database: address it by its name at `/v1/projects/$PROJECT/databases/orders-migration-test`.

## Send the request to the home region

A branch is created in the source database's home region. Any other region answers `421 wrong_region` with an `X-TerraBase-Database-Home` header naming the home region. Set `REGION` to that value and repeat the call; `api` then pins it with `X-TerraScale-Region-Pin`.

## Errors

| Response | Meaning |
| --- | --- |
| `400 invalid_request` | `name` is missing or breaks the name rules |
| `404 unknown_route` | The source database does not exist or is out of the token's reach |
| `409 name_taken` | Another database in the project already has that name |
| `409 database_limit` / `429 storage_quota` | Free a database slot or increase your allowance / resolve storage quota; never delete your recovery source just to make room |
| `421 wrong_region` | Repeat in the region named by `X-TerraBase-Database-Home` |
| `429` / `503` | `tenant_busy`, `tenant_memory`, `storage_busy`, `capacity`, `unavailable`: retry with backoff |

## Delete a branch

Confirm the target name, then run `api -X DELETE "$TERRASCALE_API_URL/v1/projects/$PROJECT/databases/orders-migration-test"` in its home region. No replica-removal PATCH is needed. `204` records logical deletion and local cleanup; remote erasure is retried. Deleting a source leaves its independent branches, but also removes the source’s backups: validate recovery before deleting it.

## Preview databases for pull requests

The `TerraScale/branch-action@v1` GitHub Action gives each pull request its own branch of a database. When a pull request opens, reopens or gets new commits, the action creates the branch `pr-<number>` from the database you choose. Later pushes keep the branch. When the pull request closes or merges, the action deletes it.

Store an API key in the repository secret `TERRASCALE_API_KEY`. `ts keys create ci` prints a key once; it has `databases:read`, `databases:write`, `documents:read` and `documents:write`.

| Input | Required | Meaning |
| --- | --- | --- |
| `api-key` | yes | The API key, from `secrets.TERRASCALE_API_KEY` |
| `database` | yes | The database to branch |
| `branch` | no | The branch name. The default is `pr-<number>` |
| `command` | no | `create`, `reset` or `delete`. Empty deletes the branch when the pull request closes and creates it otherwise. `reset` deletes the target before copying again: replacement failure loses the old target; success creates a new ULID, without transferring database-scoped keys or descendant lineage. It is destructive recreation, not an atomic reset |
| `project` | no | The project. Only organization keys need it |

The action has these outputs: `name`, `id`, `project-id` and `api-url`, which is `https://api.terrascale.tech/v1/projects/<project>/databases/<name>`.

This workflow creates the preview branch, migrates it and runs a query on it. The action installs the `ts` CLI and adds it to `PATH` for later steps. The migration step is skipped when the pull request closes.

```yaml
name: Preview database
on:
  pull_request:
    types: [opened, reopened, synchronize, closed]

concurrency: preview-database-${{ github.event.pull_request.number }}

jobs:
  branch:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v5
      - id: db
        uses: TerraScale/branch-action@v1
        with:
          api-key: ${{ secrets.TERRASCALE_API_KEY }}
          database: app
      - name: Migrate and test against the branch
        if: github.event.action != 'closed'
        env:
          TERRASCALE_API_KEY: ${{ secrets.TERRASCALE_API_KEY }}
          DATABASE: ${{ steps.db.outputs.name }}
        run: |
          ts migrate up "$DATABASE"
          ts migrate diff "$DATABASE"
          ts exec "$DATABASE" 'SELECT name FROM users LIMIT 10'
```

Outside a pull request, `ts db branch <source> <new>` creates a branch from the CLI. To apply SQL migrations to a branch, see [schema migrations](/hi/guides/migrations).

## Branches and your plan

Branches share the organization's normal plan allowances. There is no extra branch allowance. See [pricing](/hi/pricing).

Restores create databases with `branchedFrom` set too. See [backups and restore](/hi/guides/backups-and-restore).

</div>
