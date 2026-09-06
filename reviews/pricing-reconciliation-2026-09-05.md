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
