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

GitHub Actions builds and tests every PR and `main`. After all checks pass on
`main`, that same job publishes the checked static assets with
`env -u CLOUDFLARE_API_TOKEN cf deploy --prebuilt`; it does not rebuild the site.
`cloudflare.config.ts` serves the VitePress output on `docs.terrascale.tech`.
CI verifies eight hosted pages/assets against the local build and the custom
HTTP 404 page. Publication credentials come from GitHub's secret store.
The hosted check retries once after five seconds while Cloudflare propagates a
new publication; a repeated HTTP, content-type or content-hash failure fails CI.

`cf` is pinned to `1.0.0-beta.10`. Its supported `CF_API_TOKEN` credential alias
allows `CLOUDFLARE_API_TOKEN` to remain unset for every invocation. Cloudflare's
required `workersDev`, `previewUrls` and `buildContext.isPreview` fields describe
hosting/output semantics. `CLOUDFLARE_PREVIEW_BUILD` is cf's required platform
build context for branch previews; application feature switches are rejected by CI.

## Edit public content

English pages are canonical. Run `nice -n 19 npm run i18n:sync` after editing.
All current locale copies are visibly bannered English fallbacks; a reviewed
translation removes its fallback marker. Never mark untranslated content as
reviewed. Keep legal routes noindex until approved documents are available.

Pricing derives from the published catalog and documentation policy; use
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
