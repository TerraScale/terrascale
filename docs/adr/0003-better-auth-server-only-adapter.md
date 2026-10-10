# Server-only Better Auth boundary

Status: Test-only local adapter; production persistence blocked.

The production Better Auth entrypoint is server-only and raises
`security_authority_unavailable` until owning native single-writer and C06/C07
contracts admit uniqueness, compare-and-swap, one-time consume, transactions,
indexes, migrations, revocation, and regional concurrency. Leaderless writes
cannot establish those security invariants.

The existing local adapter, restricted by TS-795 to test code/test binaries,
uses Better Auth 1.7.2's official
adapter factory with SQLite serialization, true async rollback, uniqueness,
conditional updates, bounded transactions, joins and pagination. Unmodified
maintained suites exercise this test adapter. Its exported runtime path remains
inventoried for removal; no application activation is approved. Production
security still
requires real service authority, credential denial, recovery, and contention
qualification. Browser resolution of both local and production adapters is denied.
