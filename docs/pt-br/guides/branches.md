---
title: Branches
---

<!-- i18n:fallback:pt-br -->
> **Tradução pendente.** Esta página ainda não passou por revisão humana em português e mostra o conteúdo original em inglês.

<div lang="en" dir="ltr">

# Branches

A branch is a new database in the same project that starts as a consistent copy of another database, taken when you create it. After that, writes to the branch and the source are independent. Use a branch to test a migration or an import against real data without touching the source.

The published `@terrascale/sdk@0.0.3` does not wrap branching yet. The examples use `curl` against the HTTP API.

## Before you start

You need a project access token for this project with `databases:write`. A missing or invalid token answers `401`, a missing scope `403`, and another project or a database outside the token's narrowing `404`. See [authentication](/pt-br/guides/authentication).

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

`name` is required. It starts with a lowercase letter, then lowercase letters, digits, `_` or `-`, at most 63 characters, and must be free in the project.

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
| `421 wrong_region` | Repeat in the region named by `X-TerraBase-Database-Home` |
| `429` / `503` | `tenant_busy`, `tenant_memory`, `storage_busy`, `capacity`, `unavailable`: retry with backoff |

## Delete a branch

Delete a branch like any other database. Deleting the source does not delete its branches; they are independent copies.

## Branches and your plan

Branches share the organization's normal plan allowances. There is no extra branch allowance. See [pricing](/pt-br/pricing).

Restores create databases with `branchedFrom` set too. See [backups and restore](/pt-br/guides/backups-and-restore).

</div>
