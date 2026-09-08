# Public workflow reconciliation — 2026-09-07

Source packet: ts-docs base `6c142a924d470339ac15ade5a80f07a0b4d12441`,
ts-cli `9a1de71a4f3c7ec1b2b038b15a8f4476af2873a5`, and ts-typescript
`4814f13183b452fe74943321c3e717c29f1acff0`, with this uncommitted docs change.
No packages or site artifacts were published.

## Cleanup and source trace

Read the workspace GOAL cleanup requirement and public-docs AGENTS.md. Traced
package scripts, public navigation, fallback generation, starter source, CLI
entrypoints, and generated database contracts. No additional independently
removable source was confirmed. Prior qualification reviews and non-runnable
management fixtures retain evidence or pending producer work; preserved them.
Human-owned instructions were untouched.

- `create-terrascale-app/template/app/page.tsx` implements PKCE sign-in,
  `/api/auth/web-starter/delegation`, tab-memory credentials, and renewal for
  the selected project/database and posts read/write permissions.
  `.env.example` contains only public configuration. Public quickstarts still
  instructed users to enter credentials manually and put a scoped key in
  `.env.local`; now describe the implemented sign-in and delegation workflow,
  callback prerequisite, and separate exported CLI credential.
- `ts-cli/internal/cli/schema_commands.go` requires `push --database NAME`.
  Quickstarts now supply the branch explicitly before starting the app.
- `ts-cli/internal/cli/generated_contract.go` publishes database-scoped
  create/list branch, list/restore backup, and create export routes.
  `docs/snippets/exports.ts` already identifies its typed operation as
  available but unqualified. Snippets and llms now distinguish missing tenant
  and project-scoped contracts from missing export qualification.
- `ts-cli/internal/cli/dev.go` implements data-file seeds, bounded diagnostic
  polling, and schema codegen watch. Guides now describe those current
  capabilities and remove the obsolete nonexistent tenant-method example.
- Pricing source and support policy already contain the accepted shared
  organization allowance, Region multiplication, consent, and draft/owner
  approval disclosures. Corrected two abbreviated write-unit explanations to
  retain 4 KiB rounding and Region multiplication before allowances. No price,
  legal decision, support promise, or availability qualification was changed.
- Regenerated llms and English fallback translations; human-reviewed locale
  content remains protected by the existing fallback-marker policy.

## Verification

`make check` installed dependencies, built the site, and passed all seven
scaffold/decoder/remote-protocol tests. It then failed the required real cloud
sandbox gate because `TERRASCALE_SANDBOX_URL`, `TERRASCALE_API_KEY`, and
`TERRASCALE_DATABASE_ID` were unavailable. This remains unpassed qualification;
no stub was substituted for that check. Dependency audit reports two moderate
and one high finding, unchanged by these documentation edits.

Final `npm run build` and `npm run test:stub` passed: seven tests, three
local snippets, generated pricing/llms freshness, and route assertions.
`i18n-sync --check` and `git diff --check` passed. The runner’s skip message
now describes a non-runnable fixture without falsely asserting every skipped
contract is unpublished.
The real OAuth-to-delegation-to-two-browser-live-update journey remains
unqualified, as do provider/recovery/billing gates and owner publication
approval. Local rendering and protocol tests do not establish these outcomes.
