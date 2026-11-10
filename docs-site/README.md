# TerraScale public documentation

VitePress site source for SDK installation, application examples, customer
workflows and public service contracts. Hosting and publication credentials
are managed outside this source tree. A draft PR is not publication approval.

## Develop

```bash
nice -n 19 npm ci
nice -n 19 npm run dev -- --host 0.0.0.0 --port 5176
nice -n 19 npm run build
```

Run `nice -n 19 npm test` after building. CI checks public output and local links,
published catalog parity, generated pricing, SDK examples,
starter authentication and connection validation. It never downloads or runs a
TerraBase engine; engine qualification belongs in the monorepo.

## Deploy

The site lives at `docs-site/` in the public `TerraScale/terrascale` monorepo.
Run its commands from that directory. `make docs-test-fast` at the root runs
locked installation, the site build and the core tests; the `docs` job of
`.github/workflows/ci.yml` runs it for every docs change.

After a docs PR merges to `main` and the `docs` job passes, `docs-deploy.yml` builds that
revision and publishes it through Cloudflare Workers. The workflow also accepts
a manual dispatch on green `main`. It verifies `build-info.json` and the pages
and assets against `docs.terrascale.tech`. Publication uses the scoped
`CLOUDFLARE_API_TOKEN` Actions secret and `CLOUDFLARE_ACCOUNT_ID` variable.

`cf` is pinned in `package-lock.json`. Its required `workersDev`, `previewUrls`
and `CLOUDFLARE_PREVIEW_BUILD` fields describe production and preview hosting;
application feature switches remain rejected by the source gate.

## Edit public content

English is the only docs locale. Keep legal routes noindex until approved documents are available.

Subscription pricing derives from the published catalog; use
`nice -n 19 npm run canonical:sync` to refresh it. Do not invent SDK releases,
API routes, endpoint discovery or production guarantees from local evidence.

## Shared design

The [design reference](docs/.vitepress/design-reference.md) records the
committed dashboard revision, imported asset files and SDK evidence.
Update that reference and the token/asset snapshot after a reviewed dashboard
merge. The docs adapt VitePress's navigation, search, keyboard support and
mobile drawer using the dashboard's colors, fonts, spacing and flat sections.

## SQL transition

The previous downloadable examples have been removed from the site while SQL
guides and examples are updated. Source examples are not a published SQL contract.
