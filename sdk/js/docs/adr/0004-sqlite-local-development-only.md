# Fake/local SDK stores are test-only

Status: operating contract applied; exported legacy runtime removed in 0.0.16.

The [zero-configuration policy](../../AGENTS.md#zero-configuration-user-decision-2026-10-09-do-not-revert)
confines fake/local SDK stores to test code and test binaries. Version 0.0.16
removes the SQLite emulator, PostgreSQL listener client, their CLI and retired
native schema/query/framework bindings. They are no longer package entrypoints
or shipped runtime sources. Current root v1 and document codegen remain separate.

Fixture tests establish SDK behavior only. Real producer qualification requires
an immutable release binary and an authorized isolated allocation.
