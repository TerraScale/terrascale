# Review packet: public pricing wording correction

## Outcome

Commercial pricing pages now describe unbilled storage overhead without naming
internal WAL implementation, hosting provider, edge vendor, or point-of-presence
details. Customer-visible pricing, retention, allowances, guard limits,
residency, and draft/owner-approval markers remain unchanged.

## Changed owned paths

- `data/pricing.json`: replaced the internal `WAL` label with
  `service-managed storage overhead` while retaining indexes and backups as
  unbilled storage categories.
- `docs/pricing/index.md`: removed provider/internal wording from direct-member
  bandwidth and reactive-delivery explanations.
- Generated `docs/pricing.md` and 18 locale pricing/limits pages through the
  existing generator and fallback sync. No product source, legal draft,
  `GOAL.md`, or `AGENTS.md` was changed.

## Verification evidence

- `node scripts/generate-pricing.mjs --check` passed.
- `node scripts/i18n-sync.mjs --check` passed.
- `git diff --check` passed.
- `npm run build` passed; VitePress built and rendered the site in 28.73s.
- A targeted source scan found no actual `OCI`, `SQLite`, `WAL`, `Cloudflare`,
  `PoP`, or `internal` tokens in the commercial pages. The broad `WAL` regex
  can match the substring in `allowances`; that is a false positive, not an
  implementation disclosure.

## Review limits and unresolved decisions

The price sheet remains draft and requires measured unit economics and owner
approval before publication. This packet proves wording/build consistency only;
it does not qualify billing behavior, provider terms, or public launch.
