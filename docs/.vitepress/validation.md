# Refactor validation

Validated on October 12, 2026 (UTC). This report describes local documentation
checks. The draft change does not authorize merging or publishing the site.

## Passing checks

- `nice -n 19 npm run build`: VitePress build and canonical catalog parity.
- `nice -n 19 npm test`: 24 tests plus 3 retained deterministic legacy snippets;
  generated asset parity, locale freshness and unchanged C# receipt hashes.
- Public audit: 337 documentation source files, 1,020 bundled files, 331 HTML
  pages and 18,349 local links/assets/anchors. Every expected source page must
  exist. Bundled pages, search indexes, JS, CSS, JSON, SVG, text and downloadable
  examples are scanned; matches report locations/categories without values.
- TypeScript config/theme check using a temporary compiler, Node declarations
  and Vue file shim; the VitePress build also compiles the Vue component.
- Desktop (1440px), mobile (390px) and Arabic fallback quickstart checks: zero
  automated WCAG A/AA violations. Font loading, no horizontal page overflow,
  mobile drawer open/navigation/Escape/focus restoration and a focusable code
  scroller were checked. Decorative or offscreen contrast nodes can require
  manual review; the dashboard text and control palette retains its AA contrast.
- Production-bundle preview: search for pagination returns the current guide,
  Enter navigates to its heading and closes search. Public registry/pricing
  links return HTTP 200. Shared downloads stay unprefixed in locale pages.
- Seven imported dashboard token/brand/font/license files match the recorded
  design commit byte-for-byte. Whitespace check passes.

Screenshots of the built desktop/mobile home and quickstart are retained as
T3 thread artifacts, rather than committed to public source.

## Availability and limits of the evidence

The JavaScript consumer installs published npm package 0.0.3. Synthetic
transport tests verify request construction, stale-version handling, a cursor
after an empty query page, authorization denial and no mutation replay. They
are not a live-service or production-authorization test.

C# source remains identical to its retained earlier exact-package receipt.
No new exact candidate artifacts or authorized hosted credentials were
supplied. C# public NuGet returns 404, and no immutable public Go version was
qualified. Hosted registration, browser sign-in, production JWTs, automatic
discovery, fault recovery and multi-region behavior were not qualified.

All nine non-English locales remain visibly bannered English fallbacks, with
English bodies marked `lang=en` and `dir=ltr`. This is not translation review.
Accessibility checks cover representative pages and keyboard behavior, not
an exhaustive manual screen-reader review of every route.

The pre-existing root toolchain audit reports 8 advisories (6 high, 2 moderate)
across VitePress/Vite/esbuild and Wrangler dependencies. This refactor does
not upgrade that toolchain. No exposed credential was identified by the scans;
pattern scanning is not proof against every possible secret.

Protected human-owned instructions and Git history remain untouched and are
not site inputs. Dashboard and SDK source, live production routing, DNS, access
and credentials remain unchanged. Publication approval is still required.

## Pages deployment follow-up

The previous successful main deployment (GitHub Actions run `38185016494`)
used Blacksmith for both jobs, but published a static-assets Worker. The live
production domain returned HTTP 200 during inspection. This branch now uses
Pages configuration and upload commands, preserves the tested build as an
artifact, and supports a manual feature-branch preview. Main publication still
requires a reviewed merge; manual runs on main do not publish.

Local validation passes: the build/public audit, 28 tests and 3 legacy fixture
checks. Four new deployment tests reject a wrong environment/commit, unrelated
URLs, Worker output, stale assets and an HTTP 200 fallback. The Pages local
server serves all eight checked pages/assets byte-for-byte, with the generated
404 shell at HTTP 404. Browser checks show the JavaScript quickstart hydrates
without failed assets or overflow, and the missing route renders the error
message after hydration. Actionlint passes with the known Blacksmith label
allowlisted; its shellcheck integration is unavailable on this host.

The local `cf` Pages project read failed with HTTP 403, code 10000,
Authentication error. No credentials were printed or changed. The installed
`cf` CLI reports that existing Pages asset uploads require Wrangler; it does
not upload that format itself. CI uses `cf` for the project preflight and
Wrangler for the upload. Hosted Pages verification and the production custom
domain cutover remain unverified until the CI preview succeeds and the hosting
change is approved. No Cloudflare object has been changed by this follow-up.

## Complete changed-file manifest

```text
.github/workflows/ci.yml
README.md
create-terrascale-app/README.md
create-terrascale-app/qualification/ownership-migration.md
create-terrascale-app/template/connection.ts
docs/.vitepress/config.ts
docs/.vitepress/design-reference.md
docs/.vitepress/theme/DocsHome.vue
docs/.vitepress/theme/dashboard-tokens.css
docs/.vitepress/theme/index.ts
docs/.vitepress/theme/style.css
docs/.vitepress/validation.md
docs/ar/blog/benchmarks.md
docs/ar/blog/database-per-customer.md
docs/ar/blog/reads-are-free.md
docs/ar/blog/replication.md
docs/ar/changelog.md
docs/ar/concepts.md
docs/ar/concepts/pricing-philosophy.md
docs/ar/concepts/replication.md
docs/ar/concepts/sql.md
docs/ar/concepts/sync.md
docs/ar/getting-started.md
docs/ar/getting-started/csharp.md
docs/ar/getting-started/index.md
docs/ar/getting-started/javascript.md
docs/ar/guides/authentication.md
docs/ar/guides/dashboard.md
docs/ar/guides/errors.md
docs/ar/guides/index.md
docs/ar/guides/queries.md
docs/ar/guides/sdks.md
docs/ar/guides/zero-configuration.md
docs/ar/index.md
docs/ar/legal/dpa.md
docs/ar/legal/index.md
docs/ar/legal/privacy.md
docs/ar/legal/security.md
docs/ar/legal/support.md
docs/ar/legal/terms.md
docs/ar/limits.md
docs/ar/pricing.md
docs/ar/pricing/index.md
docs/ar/snippets.md
docs/ar/zero-config-inventory.md
docs/blog/benchmarks.md
docs/blog/database-per-customer.md
docs/blog/reads-are-free.md
docs/blog/replication.md
docs/changelog.md
docs/concepts.md
docs/concepts/pricing-philosophy.md
docs/concepts/replication.md
docs/concepts/sql.md
docs/concepts/sync.md
docs/de/blog/benchmarks.md
docs/de/blog/database-per-customer.md
docs/de/blog/reads-are-free.md
docs/de/blog/replication.md
docs/de/changelog.md
docs/de/concepts.md
docs/de/concepts/pricing-philosophy.md
docs/de/concepts/replication.md
docs/de/concepts/sql.md
docs/de/concepts/sync.md
docs/de/getting-started.md
docs/de/getting-started/csharp.md
docs/de/getting-started/index.md
docs/de/getting-started/javascript.md
docs/de/guides/authentication.md
docs/de/guides/dashboard.md
docs/de/guides/errors.md
docs/de/guides/index.md
docs/de/guides/queries.md
docs/de/guides/sdks.md
docs/de/guides/zero-configuration.md
docs/de/index.md
docs/de/legal/dpa.md
docs/de/legal/index.md
docs/de/legal/privacy.md
docs/de/legal/security.md
docs/de/legal/support.md
docs/de/legal/terms.md
docs/de/limits.md
docs/de/pricing.md
docs/de/pricing/index.md
docs/de/snippets.md
docs/de/zero-config-inventory.md
docs/es/blog/benchmarks.md
docs/es/blog/database-per-customer.md
docs/es/blog/reads-are-free.md
docs/es/blog/replication.md
docs/es/changelog.md
docs/es/concepts.md
docs/es/concepts/pricing-philosophy.md
docs/es/concepts/replication.md
docs/es/concepts/sql.md
docs/es/concepts/sync.md
docs/es/getting-started.md
docs/es/getting-started/csharp.md
docs/es/getting-started/index.md
docs/es/getting-started/javascript.md
docs/es/guides/authentication.md
docs/es/guides/dashboard.md
docs/es/guides/errors.md
docs/es/guides/index.md
docs/es/guides/queries.md
docs/es/guides/sdks.md
docs/es/guides/zero-configuration.md
docs/es/index.md
docs/es/legal/dpa.md
docs/es/legal/index.md
docs/es/legal/privacy.md
docs/es/legal/security.md
docs/es/legal/support.md
docs/es/legal/terms.md
docs/es/limits.md
docs/es/pricing.md
docs/es/pricing/index.md
docs/es/snippets.md
docs/es/zero-config-inventory.md
docs/fr/blog/benchmarks.md
docs/fr/blog/database-per-customer.md
docs/fr/blog/reads-are-free.md
docs/fr/blog/replication.md
docs/fr/changelog.md
docs/fr/concepts.md
docs/fr/concepts/pricing-philosophy.md
docs/fr/concepts/replication.md
docs/fr/concepts/sql.md
docs/fr/concepts/sync.md
docs/fr/getting-started.md
docs/fr/getting-started/csharp.md
docs/fr/getting-started/index.md
docs/fr/getting-started/javascript.md
docs/fr/guides/authentication.md
docs/fr/guides/dashboard.md
docs/fr/guides/errors.md
docs/fr/guides/index.md
docs/fr/guides/queries.md
docs/fr/guides/sdks.md
docs/fr/guides/zero-configuration.md
docs/fr/index.md
docs/fr/legal/dpa.md
docs/fr/legal/index.md
docs/fr/legal/privacy.md
docs/fr/legal/security.md
docs/fr/legal/support.md
docs/fr/legal/terms.md
docs/fr/limits.md
docs/fr/pricing.md
docs/fr/pricing/index.md
docs/fr/snippets.md
docs/fr/zero-config-inventory.md
docs/getting-started.md
docs/getting-started/csharp.md
docs/getting-started/index.md
docs/getting-started/javascript.md
docs/guides/authentication.md
docs/guides/dashboard.md
docs/guides/errors.md
docs/guides/index.md
docs/guides/queries.md
docs/guides/sdks.md
docs/guides/zero-configuration.md
docs/hi/blog/benchmarks.md
docs/hi/blog/database-per-customer.md
docs/hi/blog/reads-are-free.md
docs/hi/blog/replication.md
docs/hi/changelog.md
docs/hi/concepts.md
docs/hi/concepts/pricing-philosophy.md
docs/hi/concepts/replication.md
docs/hi/concepts/sql.md
docs/hi/concepts/sync.md
docs/hi/getting-started.md
docs/hi/getting-started/csharp.md
docs/hi/getting-started/index.md
docs/hi/getting-started/javascript.md
docs/hi/guides/authentication.md
docs/hi/guides/dashboard.md
docs/hi/guides/errors.md
docs/hi/guides/index.md
docs/hi/guides/queries.md
docs/hi/guides/sdks.md
docs/hi/guides/zero-configuration.md
docs/hi/index.md
docs/hi/legal/dpa.md
docs/hi/legal/index.md
docs/hi/legal/privacy.md
docs/hi/legal/security.md
docs/hi/legal/support.md
docs/hi/legal/terms.md
docs/hi/limits.md
docs/hi/pricing.md
docs/hi/pricing/index.md
docs/hi/snippets.md
docs/hi/zero-config-inventory.md
docs/index.md
docs/ja/blog/benchmarks.md
docs/ja/blog/database-per-customer.md
docs/ja/blog/reads-are-free.md
docs/ja/blog/replication.md
docs/ja/changelog.md
docs/ja/concepts.md
docs/ja/concepts/pricing-philosophy.md
docs/ja/concepts/replication.md
docs/ja/concepts/sql.md
docs/ja/concepts/sync.md
docs/ja/getting-started.md
docs/ja/getting-started/csharp.md
docs/ja/getting-started/index.md
docs/ja/getting-started/javascript.md
docs/ja/guides/authentication.md
docs/ja/guides/dashboard.md
docs/ja/guides/errors.md
docs/ja/guides/index.md
docs/ja/guides/queries.md
docs/ja/guides/sdks.md
docs/ja/guides/zero-configuration.md
docs/ja/index.md
docs/ja/legal/dpa.md
docs/ja/legal/index.md
docs/ja/legal/privacy.md
docs/ja/legal/security.md
docs/ja/legal/support.md
docs/ja/legal/terms.md
docs/ja/limits.md
docs/ja/pricing.md
docs/ja/pricing/index.md
docs/ja/snippets.md
docs/ja/zero-config-inventory.md
docs/legal/dpa.md
docs/legal/index.md
docs/legal/privacy.md
docs/legal/security.md
docs/legal/support.md
docs/legal/terms.md
docs/limits.md
docs/pricing.md
docs/pricing/index.md
docs/pt-br/blog/benchmarks.md
docs/pt-br/blog/database-per-customer.md
docs/pt-br/blog/reads-are-free.md
docs/pt-br/blog/replication.md
docs/pt-br/changelog.md
docs/pt-br/concepts.md
docs/pt-br/concepts/pricing-philosophy.md
docs/pt-br/concepts/replication.md
docs/pt-br/concepts/sql.md
docs/pt-br/concepts/sync.md
docs/pt-br/getting-started.md
docs/pt-br/getting-started/csharp.md
docs/pt-br/getting-started/index.md
docs/pt-br/getting-started/javascript.md
docs/pt-br/guides/authentication.md
docs/pt-br/guides/dashboard.md
docs/pt-br/guides/errors.md
docs/pt-br/guides/index.md
docs/pt-br/guides/queries.md
docs/pt-br/guides/sdks.md
docs/pt-br/guides/zero-configuration.md
docs/pt-br/index.md
docs/pt-br/legal/dpa.md
docs/pt-br/legal/index.md
docs/pt-br/legal/privacy.md
docs/pt-br/legal/security.md
docs/pt-br/legal/support.md
docs/pt-br/legal/terms.md
docs/pt-br/limits.md
docs/pt-br/pricing.md
docs/pt-br/pricing/index.md
docs/pt-br/snippets.md
docs/pt-br/zero-config-inventory.md
docs/public/brand/logo-terrascale-white.svg
docs/public/examples/terrabase-dotnet/Program.cs
docs/public/examples/terrabase-dotnet/TerraBaseStarter.csproj
docs/public/examples/terrabase-js/package.json
docs/public/examples/terrabase-js/quickstart.mjs
docs/public/favicon.svg
docs/public/fonts/OFL-jetbrains-mono.txt
docs/public/fonts/OFL-oxanium.txt
docs/public/fonts/jetbrains-mono-latin-wght-normal.woff2
docs/public/fonts/oxanium-latin-wght-normal.woff2
docs/public/llms.txt
docs/ru/blog/benchmarks.md
docs/ru/blog/database-per-customer.md
docs/ru/blog/reads-are-free.md
docs/ru/blog/replication.md
docs/ru/changelog.md
docs/ru/concepts.md
docs/ru/concepts/pricing-philosophy.md
docs/ru/concepts/replication.md
docs/ru/concepts/sql.md
docs/ru/concepts/sync.md
docs/ru/getting-started.md
docs/ru/getting-started/csharp.md
docs/ru/getting-started/index.md
docs/ru/getting-started/javascript.md
docs/ru/guides/authentication.md
docs/ru/guides/dashboard.md
docs/ru/guides/errors.md
docs/ru/guides/index.md
docs/ru/guides/queries.md
docs/ru/guides/sdks.md
docs/ru/guides/zero-configuration.md
docs/ru/index.md
docs/ru/legal/dpa.md
docs/ru/legal/index.md
docs/ru/legal/privacy.md
docs/ru/legal/security.md
docs/ru/legal/support.md
docs/ru/legal/terms.md
docs/ru/limits.md
docs/ru/pricing.md
docs/ru/pricing/index.md
docs/ru/snippets.md
docs/ru/zero-config-inventory.md
docs/snippets.md
docs/zero-config-inventory.md
docs/zh-cn/blog/benchmarks.md
docs/zh-cn/blog/database-per-customer.md
docs/zh-cn/blog/reads-are-free.md
docs/zh-cn/blog/replication.md
docs/zh-cn/changelog.md
docs/zh-cn/concepts.md
docs/zh-cn/concepts/pricing-philosophy.md
docs/zh-cn/concepts/replication.md
docs/zh-cn/concepts/sql.md
docs/zh-cn/concepts/sync.md
docs/zh-cn/getting-started.md
docs/zh-cn/getting-started/csharp.md
docs/zh-cn/getting-started/index.md
docs/zh-cn/getting-started/javascript.md
docs/zh-cn/guides/authentication.md
docs/zh-cn/guides/dashboard.md
docs/zh-cn/guides/errors.md
docs/zh-cn/guides/index.md
docs/zh-cn/guides/queries.md
docs/zh-cn/guides/sdks.md
docs/zh-cn/guides/zero-configuration.md
docs/zh-cn/index.md
docs/zh-cn/legal/dpa.md
docs/zh-cn/legal/index.md
docs/zh-cn/legal/privacy.md
docs/zh-cn/legal/security.md
docs/zh-cn/legal/support.md
docs/zh-cn/legal/terms.md
docs/zh-cn/limits.md
docs/zh-cn/pricing.md
docs/zh-cn/pricing/index.md
docs/zh-cn/snippets.md
docs/zh-cn/zero-config-inventory.md
examples/terrabase-js/README.md
examples/terrabase-js/package-lock.json
examples/terrabase-js/package.json
examples/terrabase-js/quickstart.mjs
examples/terrabase-js/quickstart.test.mjs
package.json
scripts/check-build.mjs
scripts/check-pages.mjs
scripts/check-pages.test.mjs
scripts/check-public.mjs
scripts/generate-llms.mjs
scripts/generate-pricing.mjs
scripts/i18n-sync.mjs
scripts/public-audit.mjs
scripts/public-audit.test.mjs
scripts/run-snippets.mjs
scripts/stub-sandbox.mjs
wrangler.jsonc
```
