# Fake/local SDK stores are test-only

Status: Operating contract updated by TS-795; runtime removal pending.

The [zero-configuration policy](../../AGENTS.md#zero-configuration-user-decision-2026-10-09-do-not-revert)
supersedes the earlier local-development opt-in decision. Fake/local SDK stores
belong only in test code and test binaries, never an exported application mode
or a customer emulator workflow. Existing `@terrascale/sdk/local` and listener
paths remain inventoried for their owning removal thread; this ADR does not
claim those paths have already been removed.

Isolated adapter tests may check transactions, rollback, uniqueness,
conditional updates, bounded queries and pagination. Their state tokens and
results do not prove producer compatibility, regional durability or security
authority. Preserve credential validation and environment safeguards until
runtime removal. Real producer qualification must use the immutable release
binary and an authorized isolated allocation, with no fake substitute.
