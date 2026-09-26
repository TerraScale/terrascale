# ts-typescript ADR index

These records define the public TypeScript SDK boundary, local development
runtime, reactive protocol, and package release gates. The workspace
[`../../../GOAL.md`](../../../GOAL.md) remains authoritative. Status does not replace implementation or live
qualification tests.

| ADR | Status | Decision |
| --- | --- | --- |
| [0001](0001-support-current-node-lts.md) | Accepted; qualification pending | Support Node.js 22 and 24 and current major browsers. |
| [0002](0002-typed-public-sdk-boundary.md) | Accepted boundary; qualification pending | Expose named typed public operations and no generic transport. |
| [0003](0003-better-auth-server-only-adapter.md) | Accepted boundary; qualification pending | Keep Better Auth mapping server-only and typed. |
| [0004](0004-sqlite-local-development-only.md) | Accepted | SQLite is explicit local development only. |
| [0025](0025-public-messagepack-manifest.md) | Accepted manifest; transport qualification pending | Generate bounded public MessagePack codecs from one manifest. |
| [0032](0032-public-protocol-bounds.md) | Accepted bounds; service parity pending | Apply workspace public limits and fail closed on hostile input. |
| [0033](0033-sdk-production-release-gates.md) | Accepted release boundary; live qualification pending | Promote only the exact verified npm artifact and workspace-qualified SDK. |
