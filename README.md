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

There is no CI and no test suite.

## Deploy

Cloudflare Pages builds `main` from Git: build command `npm run build`,
output directory `docs/.vitepress/dist`, custom domain `docs.terrascale.tech`.

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

## Examples

The [JavaScript example](examples/terrabase-js/README.md) pins published npm
version 0.0.3. The [C# example](examples/terrabase-dotnet/README.md) consumes an
exact candidate package. Their downloadable public assets are generated from
an explicit file list.
