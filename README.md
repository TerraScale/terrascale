# TerraScale public documentation

VitePress site source for SDK installation, application examples, customer
workflows and public service contracts. Hosting and publication credentials
are managed outside this source tree. A draft PR is not publication approval.

## Develop and verify

```bash
nice -n 19 npm ci
nice -n 19 npm run dev -- --host 0.0.0.0 --port 5176
nice -n 19 npm run build
nice -n 19 npm test
```

Local builds, tests and watchers run at low CPU priority. CI runs at its normal
priority. The build checks canonical public catalogs, generates an allowlist
of downloadable examples, builds the site and audits every bundled text file
for private material and broken local links/assets/anchors.

`npm test` verifies generated files, locale freshness, current JavaScript
example behavior, privacy redaction and the retained legacy fixture suite.
Synthetic transport checks are local evidence, not hosted-service qualification.
The existing C# consumer's receipt is checked against its unchanged source hashes.

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

## Examples and qualification

The [JavaScript example](examples/terrabase-js/README.md) pins published npm
version 0.0.3. The [C# example](examples/terrabase-dotnet/README.md) consumes an
exact candidate package. Their downloadable public assets are generated from
an explicit file list; package caches, tests and credentials are not copied.

A real run requires an authorized isolated allocation and synthetic data.
Retained legacy `test:remote` and candidate `qualify:dotnet` commands require
their own verified inputs. Never replace missing service access with a fake
qualification result. Do not merge or publish until privacy checks pass and
publication approval is recorded.
