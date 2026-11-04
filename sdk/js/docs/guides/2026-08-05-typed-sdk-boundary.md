# Typed SDK boundary

Use `createTerraBaseClient` for SQL and the separate `ts-auth` entrypoint for
hosted identity. SQL arguments accept exact bigint integers; integers beyond 2^53
arrive as `ExactDecimal`. Send a mutation once and reconcile uncertain outcomes
before retrying it yourself.

Management,
administration and production Better Auth remain explicit blocked capabilities
until their owning service contracts are admitted. The local SQLite entrypoint
is restricted to test code and test binaries by the TS-795 operating contract.
Its exported runtime remains an implementation removal gap, not an approved
application setup path or qualification substitute.

Consumers can import only declared package entrypoints. Private HTTP, native
codec implementation files, direct engine and internal control transports are
not public escape hatches. See [the boundary decision](../adr/0002-typed-public-sdk-boundary.md)
and [release requirements](../release.md).
