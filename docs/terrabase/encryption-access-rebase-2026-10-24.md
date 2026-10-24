# Encryption after collection access rules — 2026-10-24

PR #469 merged as `b20946563700e5ed421abbeb2777675aa6db6462`, advancing main to storage format 16. Encryption is rebased onto that commit and uses format 17 (main + 1). The #469-first queue hold is satisfied; fresh exact-head CI and a final main/version check remain required before enqueueing. No reset or activation is performed by this PR.

All 52 encryption commits replayed. The only conflicts were two historical edits to `StorageFormat.cs`; both preserve main's collection-access-proof/feed-generation entry at 16 and encryption at 17. Range comparison shows the other 50 patches unchanged.

The union retains the access-rule feed columns and transaction delegates, protocol-2 receiver chaining, write durability behavior and auth test transport headers, the SYSTEM rewrite test's backup-reader isolation, new auth settings collections and the current CI SDK pin. Main's replication receiver/sender, document authorization and CI workflow match main exactly. Encryption's key handling, regional shared fixture master, reader reset, healthy-rewrite custody and recovery safeguards remain present. Native sources, delivery pipeline and patch mirror have no new base changes.

CI on historical head `2725895a1` passed native, libs, auth, TerraBase, both Slow suites and required `test`. The 55/55 focused fixture proof remains at `839065c23`; neither result validates the new base. No additional local build/test or performance run was performed for this rebase. GitHub CI validates the rebased head.
