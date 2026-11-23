# A database per pull request

Sources: [branches guide](https://docs.terrascale.tech/guides/branches),
[action README at the pin](https://github.com/TerraScale/branch-action/blob/d07e8808dc73537a2fc7c566de86d5230607076a/README.md),
[migration CI](https://docs.terrascale.tech/guides/migrations).

## Prerequisites and publication gate

- [ ] Use synthetic data; a preview copies the source database.
- [ ] Have two database slots and capacity for concurrent PRs.
- [ ] Store a project API key as repository secret TERRASCALE_API_KEY with
  databases:read, databases:write, sql:read and sql:write.
- [ ] Verify guarded create/delete routes and the pinned CLI are deployed.

The action README puts this source contract under an **unreleased publication
hold**. Existing v1 lacks its guards. The workflow pins source and is a template
to activate after prerequisite verification, not a claim that v1 or required
server routes are ready. Do not publish action tags during app setup.

The public branches guide says source builds a CLI; the pinned action instead
downloads ts 0.0.58 with per-platform SHA-256 verification. Use its installed ts.

## Complete workflow

Save as .github/workflows/preview-database.yml. This assumes ts migration files
under migrations/ and source database app.

~~~yaml
name: Preview database
on:
  pull_request:
    types: [opened, reopened, synchronize, closed]
permissions:
  contents: read
concurrency: preview-database-${{ github.event.pull_request.number }}
jobs:
  preview:
    if: github.event.pull_request.head.repo.full_name == github.repository
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@fbc6f3992d24b796d5a048ff273f7fcc4a7b6c09
      - id: database
        uses: TerraScale/branch-action@d07e8808dc73537a2fc7c566de86d5230607076a
        with:
          api-key: ${{ secrets.TERRASCALE_API_KEY }}
          database: app
          ttl: 7d
      - name: Apply and check branch migrations
        if: github.event.action != 'closed'
        shell: bash
        env:
          DATABASE_URL: ${{ steps.database.outputs.connection-url }}
          BRANCH_ID: ${{ steps.database.outputs.id }}
        run: |
          set -euo pipefail
          set +x
          TERRASCALE_API_KEY="$(python3 -c 'import os; from urllib.parse import urlsplit, unquote; print(unquote(urlsplit(os.environ["DATABASE_URL"]).password or ""))')"
          test -n "$TERRASCALE_API_KEY"
          export TERRASCALE_API_KEY
          ts migrate up "$BRANCH_ID"
          ts migrate status "$BRANCH_ID"
          ts sql "$BRANCH_ID" 'SELECT 1'
~~~

The URL password is the newly issued branch-only key. Extracting it into a shell
variable preserves ts checksum/history handling without granting migration steps
source access. Do not print the variable or URL; the action masks both.
Sources: [branch URL contract](https://docs.terrascale.tech/guides/branches),
[CLI URL construction](https://github.com/TerraScale/terrascale/blob/main/sdk/go/cmd/ts/branches.go).

Same-repository contributors with workflow access can change workflow code;
reserve management secrets for trusted contributors. Fork PRs lack repository
secrets and are skipped. Keep pull_request, not pull_request_target. Add app checks
using the scoped connection, without passing management credentials.
Sources: [branches](https://docs.terrascale.tech/guides/branches),
[GitHub PR events](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#pull_request).

## Lifecycle and failures

- Open/reopen creates; new pushes reuse the independent copy and preserve data.
- Close/merge conditionally deletes verified previews; no connection URL is emitted.
- Reuse does not extend TTL. Delete expired previews before creating replacements.
- Other outputs: name, id, project-id and api-url.
- Organization keys need project input specifying the project ID.
- Reset creates a new generation; old credentials remain on the old database.
  It is not an atomic swap. Old generations consume quota until cleanup.
- Serialize per PR; overlapping cleanup/creation can leave previews alive.
  Conditional cleanup can partly finish: inspect identities before retrying.
- Unsupported guarded routes fail without unconditional deletion.
- After merge, apply the same history to app via the trusted production workflow.
  Database branches do not merge data.

Source: [action contract](https://github.com/TerraScale/branch-action/blob/d07e8808dc73537a2fc7c566de86d5230607076a/README.md).
