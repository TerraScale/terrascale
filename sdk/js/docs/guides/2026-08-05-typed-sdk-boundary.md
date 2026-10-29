# Typed SDK boundary

Use `createTerraBaseClient` for native documents and the separate `ts-auth`
entrypoint for hosted identity. Native operations accept canonical scoped UUIDs,
bigint quantities and discriminated values. Keep complete conflicts and causal
context. Prepare a mutation once and resolve uncertain outcomes through status.

Schema declarations and parameterized SQL descriptors are pure local builders.
They neither provision server schemas nor imply listener capabilities. Management,
administration and production Better Auth remain explicit blocked capabilities
until their owning service contracts are admitted. The local SQLite entrypoint
is restricted to test code and test binaries by the TS-795 operating contract.
Its exported runtime remains an implementation removal gap, not an approved
application setup path or qualification substitute.

Consumers can import only declared package entrypoints. Private HTTP, native
codec implementation files, direct engine and internal control transports are
not public escape hatches. See [the boundary decision](../adr/0002-typed-public-sdk-boundary.md)
and [release requirements](../release.md).
