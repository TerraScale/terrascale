# TerraBase

TerraBase is TerraScale's SQL database service, implemented in C# on .NET 11. Customer data lives in SQL tables using the SQLite dialect. Clients use HTTP SQL, SQL over WebSocket or PostgreSQL drivers.

The product goal is to write in any region and read in any region, with free reads. SQL read fair use and monthly write caps follow the [operation allowance contract](operation-allowance.md). Shape reads are free and unmetered. Runtime availability and commit guarantees follow the implemented routing paths below; the goal alone is not evidence of a production rollout.

## SQL and regions

- [Native SQL](sql/native-sql.md): statements, transactions, errors and sandbox.
- [SQL WebSocket](sql/websocket.md).
- [Native full-text qualification](sql/full-text-native.md).
- [Write routing](sql/write-routing.md): regional decisions and home forwarding.
- [Home apply](home-apply.md): conflict resolution and transaction states.
- [Local overlay](local-overlay.md): regional writes and push to home.
- [Region replicas](region-replicas.md) and [replica status](replica-status.md).
- [Placement](placement.md) and [copy placement during recovery](recovery-copy-placement.md).
- [Shape reads](shape-reads.md), [table read grants](shape-read-grants.md) and [live shape reads](free-reads-live.md).
- [Regional shape tails and capture retention](region-shape-tails-and-cdc-retention.md).

## API and security

- [HTTP API](api.md) and [interactive reference](api-reference.md).
- [Management API](management-api.md).
- [TLS and authentication](transport-security.md).
- [API-key ownership](customer-api-key-ownership.md) and [revocation](key-revocation.md).
- [Cross-project isolation](cross-project-errors.md).
- [SQL catalog](sql-catalog.md), [plain-row system records](decisions/2026-11-04-plain-row-storage.md) and [IDs](id-contract.md).

Public routes use `/v1`. ts-auth issues credentials; TerraBase validates project tokens offline and enforces the project, database and scope boundaries. `sql:read` permits SQL reads; writes and DDL also require `sql:write`. End-user tokens read shapes only through table read grants.

The public listener serves `/openapi/v1.json` and `/scalar/`. `/healthz` is liveness and `/readyz` is readiness; neither returns tenant data. The administrative listener does not serve the public reference.

## Operations

- [Zero-configuration contract](zero-configuration.md), [OCI hosting](../oci.md) and [ts-orch](ts-orch.md).
- [Local host](local-host.md) and [region discovery](region-catalog.md).
- [Continuous backup](backup.md), [crypto-shredding](crypto-shredding.md) and [deletion retention](deletion-retention.md).
- [Resource admission](qos.md), [shared-process tenancy](shared-process-tenancy.md) and [disk admission](disk-admission.md).
- [Database allowances](plan-database-allowance.md), [agent databases](agent-databases.md) and [database rename](database-rename.md).
- [Clock admission](hlc-clock.md), [node pressure](node-pressure.md) and [metrics export](metrics-export.md).

Derive region, node and peers from `TS_REGION`, `TS_NODE` and `TS_PEERS_FILE`. OCI Vault supplies runtime secrets; GitHub Actions holds CI secrets. Database keys, backups, authorization, revocation and recovery safeguards remain mandatory.

## Development and shipping

Use the exact SDK in `global.json` and the root Makefile. Run heavy work through `ts-heavy-any`; ship from an isolated checkout with `ts-ship`. Fast validation is `make terrabase-test-fast`; Slow is `make terrabase-test-slow`. See [CI](ci.md) and [Fast budgets](fast-ci-budgets.md). Deployment requires green main; launch formalities remain [deferred](../decisions/go-live.md).
