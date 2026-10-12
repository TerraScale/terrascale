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

## Cloudflare Pages deployment

CI and upload jobs use `blacksmith-2vcpu-ubuntu-2404` with Blacksmith's Node cache.
The upload receives the exact built site that passed tests and the public audit;
it does not rebuild the site. Cloudflare credentials come from the GitHub
platform secret store. No credentials or account IDs belong in this repository.

The Pages project is `ts-docs`, with production branch `main` and a direct-upload
source. A preflight uses `cf` to verify that contract. The current `cf` CLI
explicitly directs existing Pages asset uploads to `wrangler pages deploy`,
which this repository uses. `nice -n 19 npm run preview` serves the built site
with Pages routing locally.

1. Review the draft and complete the privacy checks before publication approval.
2. Run the CI workflow manually on the feature branch to verify a Pages preview.
3. Check its deployment summary: commit, Pages environment, URL and hosted checks.
4. After approval, merge to `main` to publish to the Pages production project.

Hosted checks require the expected Pages project, environment and commit;
eight representative pages/assets must match the tested build byte-for-byte.
The generated VitePress error page must return HTTP 404. A preview cannot target
production. A missing project or denied API access fails the job without creating
a Worker or changing DNS.

Moving `docs.terrascale.tech` from the existing Worker to Pages is a separate
approved hosting change after preview verification. Repository configuration
does not transfer a Worker custom domain. No deployment here deletes the Worker,
changes DNS, or modifies credentials.

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
