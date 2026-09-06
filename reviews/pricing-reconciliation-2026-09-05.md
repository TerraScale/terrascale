# Pricing reconciliation — 2026-09-05

Draft commercial documentation now uses shared organization allowances for databases
and branches, Region multiplication before allowances, subscription-only onboarding
promotion, explicit excess-usage consent, anniversary resets, subscription changes,
write caps with continuing storage costs, and separate card/unpaid-storage grace.
Deletion protection and idle-Free lifecycle are described with their exceptions.
Unsupported support, SLA, status-page, and incident-response promises were removed
from pricing and draft terms/support. Final prices, notices, physical deletion timing,
and support benefits remain subject to evidence and owner approval; nothing published.

Cleanup traced package scripts, Forgejo inputs, and repository references: the unused
scripts/test.mjs duplicated scripts/check-build.mjs and asserted retired routes.
Removed that duplicate; the package check remains the active entrypoint. Historical
reviews, architecture artifacts, human instructions, and management fixtures remain.

Verification on the working tree:

- npm run build passed after the final generated-page update.
- npm run test:stub passed: four decoder tests and three local snippet smoke tests.
- Pricing/limits and llms freshness assertions passed; i18n fallback sync --check and
  git diff --check passed. Human-reviewed translation markers are preserved.
- make check ran npm ci, site build, and four decoder tests successfully, then failed
  the required real sandbox gate: TERRASCALE_SANDBOX_URL, TERRASCALE_API_KEY, and
  TERRASCALE_DATABASE_ID are unavailable. This is an unpassed qualification gate.
- Tenant, branch, backup, and export fixtures remain explicitly skipped because their
  owning public contracts are unpublished. Stub success does not qualify them.
- npm ci reported three dependency audit findings (two moderate, one high); dependency
  upgrades were outside this commercial reconciliation.

## Starter follow-up

The Next starter now uses a scoped development credential held in tab memory,
with disconnect/reload clearing it, instead of compiling NEXT_PUBLIC API keys.
It writes posts through the actual SDK documents.put API and displays canonical
live result deltas. An uncertain write retains its document/idempotency identity
and original value for retries. Subscription and write requests abort on teardown.
Application sign-in remains unmet, not replaced by the development credential form.
The human-owned template AGENTS.md retains obsolete V2/no-CRUD instructions and
requires human correction.

A fresh scaffold in /tmp/terrascale-starter-check-J0ljG5 installed a local packed
SDK 0.3.0 dist artifact. TypeScript checking and Next 15.5.25 production build
passed (137 kB reported first-load JS). This does not qualify a registry release.
Playwright (Browser plugin unavailable) verified mobile rendering, credential
entry, authentication failure, disabled writes, disconnect/reload, no stored
credentials, and no browser runtime errors. A second test used the actual SDK
and generated canonical MessagePack frames to verify a document write displayed
in two browser tabs. API/CDN responses were intercepted: these are local UI
checks, not real cloud or identity integration evidence. Browser scripts remain
at /tmp/terrascale-starter-check-J0ljG5/{browser-check,live-check}.cjs.

The combined local test command passed seven tests (scaffold plus six wire/decoder
cases), three stub snippets, and pricing/llms assertions. Real cloud remains
blocked by credentials and production identity/delegation qualification.

## Public navigation cleanup

Traced VitePress page trees, English navigation, every fallback locale, quickstart
links, llms route generation, and build assertions. Removed public engines and
why-sqlite pages/routes; the storage design draft and benchmark methodology remain
under reviews/architecture, outside the VitePress source root, explicitly marked
historical/unqualified. Internal storage, replication, and incremental-view decisions
also remain in TerraBase ADRs 0040, 0041, and 0044; native backup work is ADR 0046.
No measured qualification record or human-reviewed translation was removed.

Public replication/sync pages retain durability, causal-session, conflict, access,
checkpoint, and billing behavior without storage-file/transport implementation
prose. Blog and home claims now distinguish intended behavior from unmeasured
latency, creation time, capacity, and idle cost. Public SQL-dialect documentation
is retained because it describes customer query syntax. Unbilled WAL exclusion
is retained as a pricing disclosure. Removed deferred status links across locales.
The backup fixture now correctly states that backups are unbilled.

Final npm run build and npm run test:stub passed (seven tests, three local snippets,
route/retired-page assertions, pricing/llms freshness). i18n --check and diff checks
passed. The retired pages are also absent from generated HTML. This documentation
check does not change the previously failed real cloud qualification gate.
