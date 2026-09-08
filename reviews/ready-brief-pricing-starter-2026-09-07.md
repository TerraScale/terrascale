# Ready briefs: public pricing disclosure and starter qualification gap

Prepared 2026-09-07 from `GOAL.md`, `ts-docs/AGENTS.md`, the current docs
source, and the existing pricing reconciliation. This is an execution note;
it does not establish acceptance or owner approval.

## Brief A — remove internal implementation detail from public pricing pages

### Outcome and current gap

Keep the accepted commercial behavior public and runnable while removing
implementation/provider detail forbidden by `ts-docs/AGENTS.md` ("public docs
repo ... should not have internal information ... like which database we are
using internally"). The source of the generated pages is
`ts-docs/data/pricing.json`, rendered by `ts-docs/scripts/generate-pricing.mjs`.

Current public pricing pages expose `OCI` and Cloudflare processing details in
the placement section (`docs/pricing/index.md`, all locale copies), and say
"bytes leaving OCI" in the direct-member guard explanation. The generated
pricing page also discusses WAL as an internal storage artifact. These details
are not needed to explain the billed dimensions or customer residency promise.
The legal/privacy/DPA drafts separately contain provider disclosure placeholders;
this brief does not alter those legal drafts.

The accepted rules to preserve are in `GOAL.md` Commercial model and Retention
sections: writes and logical storage are billed; reads, reactive delivery,
egress, subscribers, replay, TTL expiry, and webhooks are unbilled within
disclosed guards; 4 KiB write-unit and selected-Region multiplication; logical
storage includes retained history and selected-Region multiplication; PITR is
1/7/30/35 days; history defaults to seven days and is plan-capped; Free pause
is 30 idle days and deletion eligibility is after 90 idle days with notices;
paid databases are never idle-reaped.

### Ownership and write scope

One docs owner may write `ts-docs/data/pricing.json`,
`ts-docs/scripts/generate-pricing.mjs`, and the generated pricing/limits pages
only through the existing generator. The same owner owns generated locale
outputs if the generator/check requires regeneration. Do not edit `GOAL.md`,
any `AGENTS.md`, product repositories, legal drafts, or unrelated translations.
Record command output and the changed-path list beside this note or in the
coordinator task record.

### Chosen correction

Change customer-facing wording to refer to "direct member read bandwidth"
without naming the hosting provider, and describe placement as "data remains
within the Regions selected for the database; placement is enforced for
replication, backup, restore, export, and branches." Remove WAL/provider/edge
implementation wording from the generated commercial page while retaining
the customer-visible fact that indexes and backups are not billed and reactive
delivery is free. Do not weaken the existing draft/owner-approval markers.

### Acceptance

1. `rg -n -i 'OCI|SQLite|WAL|Cloudflare|PoP|internal' docs/pricing docs/limits`
   returns no implementation/provider hits introduced by the commercial page.
2. The generated English and locale outputs remain byte-consistent with the
   source (`node scripts/generate-pricing.mjs --check`), and existing i18n
   freshness checks pass.
3. A source review confirms the examples still show 5 KiB × 3 Regions = 6
   write units, 100 MB × 3 Regions = 300 MB allowance, all plan prices,
   retention, grace, consent, and guard disclosures unchanged.
4. `npm run build` and the existing focused docs checks pass when the shared
   docs build owner has capacity. No real-cloud check is needed for wording.

### Forbidden shortcuts and decisions

Do not remove residency, deletion, billing, or support-availability behavior
just to hide provider names. Do not publish the draft price sheet as approved.
If legal owner wants provider names in a public legal disclosure, that is a
separate owner decision; it does not justify exposing them in commercial
pricing copy.

## Brief B — qualify the actual starter sign-in-to-live-update journey

### Outcome and current gap

The starter code now implements OAuth PKCE, short-lived scoped branch access,
typed `client.documents.put`, and a V1 sync client in
`create-terrascale-app/template/app/page.tsx` and `auth.ts`. The shipped
journey remains unqualified: `create-terrascale-app/qualification/README.md`
requires a real deployed app, auth service, cloud branch, two eligible
accounts, registered callback, source/artifact digests, and
`TERRASCALE_STARTER_JOURNEY_CONFIG`. The existing reconciliation records that
real credentials/services are unavailable. Local interception/stub/browser
checks cannot establish acceptance.

The template assistant note is stale and human-owned:
`create-terrascale-app/template/AGENTS.md` still prescribes
`TerraScaleSyncV2Client` and says typed document CRUD is absent, while the
template README and implementation use `TerraScaleSyncV1Client` and
`client.documents.put`. The owner must correct that file; the docs worker must
not edit it.

### Ownership and dependencies

The starter/docs owner owns the qualification fixture record, browser harness
evidence, template README/source alignment, and generated docs. Dashboard/auth,
SDK, CLI, and database owners own the deployed endpoints and artifacts. The
shared docs build owner owns one `make check`/focused docs run; do not start a
competing build in the shared checkout. This task is blocked until the
coordinator supplies the real fixture and those producer artifacts.

### Executable acceptance

Run from `ts-docs/create-terrascale-app` with the private fixture:

    npm install
    npx playwright install chromium
    TERRASCALE_STARTER_JOURNEY_CONFIG=/private/starter-fixture.json npm run qualify:browser

The result must have `complete: true` and source/artifact identifiers for
starter, SDK, database, and auth. It must prove two independent real sign-ins
share a view, two-way writes appear in both browsers, refresh rotation works,
disconnect/catch-up works, revocation stops one session within the declared
bound while the peer continues, and a fresh sign-in resumes writes. Preserve
only credential-free result evidence; never commit the fixture, passwords,
tokens, or traces. A passing local build or intercepted browser run is not
acceptance.

### Required owner decision / correction

The human owner must update `template/AGENTS.md` to the actual V1/document
API guidance before shipping the scaffold. If the producer contracts change
again, stop and reconcile the implementation, README, assistant note, and
qualification harness together; do not add compatibility aliases for V2 or
the removed CRUD surface.

## Queue state

Brief A is ready and independently actionable. Brief B is prepared but
blocked on external qualification credentials and deployed producer artifacts;
its stale human-owned instruction is an owner decision. No worker report here
is acceptance. No implementation or build handle is active for these briefs.

## Confirmed recurring-error entries

- **Stale surface guidance after contract changes (confirmed).** Cause:
  `template/AGENTS.md` retained V2/no-CRUD instructions after the template
  moved to V1 sync and typed document writes; source examples are
  `template/app/page.tsx:169,218` and `template/README.md`, versus
  `template/AGENTS.md` lines describing V2/no CRUD. Correction: owner updates
  the human-owned note in the same contract change. Prevention: include
  assistant notes and generated/public starter README in the contract
  reconciliation checklist; do not add compatibility aliases.
- **Local/stub evidence mistaken for public qualification (confirmed).** Cause:
  local browser interception and stub snippet checks can pass without auth,
  cloud branch, or producer artifacts. Correction: require the connected
  harness result with `complete: true` and credential-free source/artifact
  evidence. Prevention: keep missing-fixture behavior failing and label local
  checks as non-qualification, as specified in
  `create-terrascale-app/qualification/README.md` and the 2026-09-05 pricing
  reconciliation.
