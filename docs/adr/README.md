# ts-js ADR index

These records define the public JavaScript SDK boundary, test fixtures,
reactive protocol, and package release gates. The repository
[AGENTS.md](../../AGENTS.md) defines the zero-configuration operating contract. Status does not replace implementation or live
qualification tests.

| ADR | Status | Decision |
| --- | --- | --- |
| [0001](0001-support-current-node-lts.md) | Accepted; qualification pending | Support Node.js 22 and 24 and current major browsers. |
| [0002](0002-typed-public-sdk-boundary.md) | Accepted boundary; qualification pending | Expose named typed public operations and no generic transport. |
| [0003](0003-better-auth-server-only-adapter.md) | Accepted boundary; qualification pending | Keep Better Auth mapping server-only and typed. |
| [0004](0004-sqlite-local-development-only.md) | Updated by TS-795; removal pending | Fake/local SDK stores are test-only. |
| [0005](0005-javascript-with-jsdoc-types.md) | Accepted | Ship JavaScript typed with JSDoc; generate declarations only. |
| [0032](0032-public-protocol-bounds.md) | Accepted bounds; service parity pending | Apply workspace public limits and fail closed on hostile input. |
| [0033](0033-sdk-production-release-gates.md) | Accepted | CI publishes the exact packed tarball automatically after verification. |
