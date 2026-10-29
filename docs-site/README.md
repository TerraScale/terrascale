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
published catalog parity, generated pricing and locale copies, SDK examples,
starter authentication and connection validation. It never downloads or runs a
TerraBase engine; engine qualification belongs in the monorepo.

## Deploy

The site lives at `docs-site/` in the public `TerraScale/terrascale` monorepo.
Run its commands from that directory. `make docs-test-fast` at the root runs
locked installation, the site build and the core tests on a dev host through
`ts-ship`.

Before shipping a docs change, qualify its clean commit on a dev host:

```bash
ts-heavy-any bash docs-site/scripts/prepare-deployment.sh --repo terrascale --branch <staging-ref>
```

This audits all locked npm graphs, builds the site, runs the full examples and
starter checks, and uploads immutable artifacts under `docs/qualified-<sha>`.
It does not deploy. After `ts-ship` pushes that same commit and monorepo CI is
green, `docs-deploy.yml` publishes the checked artifact through Cloudflare
Workers. The workflow also accepts a manual dispatch on `main`. It verifies
`build-info.json` and the checked pages/assets against `docs.terrascale.tech`.
Publication uses the existing scoped `CLOUDFLARE_API_TOKEN` Actions secret and
`CLOUDFLARE_ACCOUNT_ID` variable.

For an operator deployment after green main, use the existing host's Cloudflare
authentication, without moving its broader user token into CI:

```bash
cd docs-site
cf deploy --prebuilt --message "$(git rev-parse HEAD)"
node scripts/check-hosted.mjs
```

`cf` is pinned in `package-lock.json`. Its required `workersDev`, `previewUrls`
and `CLOUDFLARE_PREVIEW_BUILD` fields describe production and preview hosting;
application feature switches remain rejected by the source gate.

## Edit public content

English pages are canonical. Run `nice -n 19 npm run i18n:sync` after editing.
All current locale copies are visibly bannered English fallbacks; a reviewed
translation removes its fallback marker. Never mark untranslated content as
reviewed. Keep legal routes noindex until approved documents are available.

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
