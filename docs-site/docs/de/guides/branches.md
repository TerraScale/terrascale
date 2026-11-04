---
title: Branches
---

<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

<div lang="en" dir="ltr">

# Branches

A branch is an independent database containing a snapshot of its source. Use one
per Git branch or pull request to test migrations and changes against seeded data.
Later writes to either database stay isolated. Each branch counts toward your
organization's database allowance, so the source and branch need two available
database slots.

## Create a branch

Use a database name starting with a lowercase letter and containing up to 63
lowercase letters, digits, underscores or hyphens. Map Git names such as
`feature/orders` to a database name such as `feature-orders`.

```sh
ts db branch create feature-orders --from app --ttl 7d
ts sql feature-orders 'SELECT * FROM orders LIMIT 10'
ts db branch list
```

The CLI defaults to a seven-day TTL. `--ttl` accepts days (`7d`) or whole-second
Go durations (`24h`, `90m`, `60s`), from one minute to 365 days. A branch does not
inherit its parent's expiry. Expiry permanently deletes the branch and its data;
it does not delete the parent. Listing includes each branch's source ID and expiry.

Creation resolves the source's immutable ID and sends it with the fork request.
If the source was replaced, the server rejects the request instead of copying a
different database. Automation can supply a previously inspected source ID with
`--expected-id <ULID>`. Servers without the guarded route fail without a fallback.

## Connect to a branch

With `TERRASCALE_API_KEY` set, issue a connection URL for a branch:

```sh
# Capture stdout securely: the URL contains a newly issued secret.
DATABASE_URL=$(ts db branch url feature-orders)
export DATABASE_URL
PGDATABASE="$DATABASE_URL" psql -c 'SELECT 1'
```

The URL uses TLS certificate verification and the branch's immutable ID. Its key
allows reads and writes only to that branch. It expires after at most seven days,
or sooner if the branch expires. Issuing another URL creates a fresh key. Never
print the URL or store it in Git. Branch creation and listing also work with
`ts auth login`; URL issuance requires an API key that can issue scoped keys.

Use `--json` for structured results. Create returns database metadata, list returns
an array, and URL issuance returns `connectionUrl` and `expiresAt`.

## Delete a branch

```sh
ts db branch delete feature-orders --yes
```

The CLI verifies that the database is a branch, then conditionally deletes its
observed immutable ID. A same-name replacement fails instead of being deleted.
Use `--expected-id <ULID>` to require an ID already captured by automation.
Deletion preserves the parent and other branches.

## Pull request previews

The [branch action](https://github.com/TerraScale/branch-action) creates a branch
on PR open, reuses it on new pushes, and deletes it on close or merge. Each preview
name binds the repository, PR, project and source ID. Pin the action to a full
commit SHA in your workflow; `main` below selects the current source, not a release.

Store a project API key with database read/write and document read/write scopes
as the repository secret `TERRASCALE_API_KEY`.

```yaml
name: Preview database
on:
  pull_request:
    types: [opened, reopened, synchronize, closed]
permissions:
  contents: read
concurrency: preview-database-${{ github.event.pull_request.number }}
jobs:
  preview:
    runs-on: ubuntu-latest
    steps:
      - id: database
        uses: TerraScale/branch-action@main
        with:
          api-key: ${{ secrets.TERRASCALE_API_KEY }}
          database: app
          ttl: 7d
      - if: github.event.action != 'closed'
        env:
          DATABASE_URL: ${{ steps.database.outputs.connection-url }}
        run: PGDATABASE="$DATABASE_URL" psql -c 'SELECT 1'
```

The action masks the connection URL and its scoped key before exporting them.
Other outputs are `name`, `id`, `project-id` and `api-url`. Closed PRs produce no
connection URL. Existing previews retain their original TTL; new pushes do not
extend it. Delete an expired preview before creating a replacement.

Fork PRs do not receive repository secrets. Keep this workflow on `pull_request`
and serialize runs per PR. Use synthetic seed data and run migrations with the
scoped branch URL. The action's source builds a pinned CLI from the monorepo;
no new CLI or action release is required.

</div>
