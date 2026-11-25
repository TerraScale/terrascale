import { readFileSync, writeFileSync } from 'node:fs';

const root = new URL('../', import.meta.url);
const { plans, metering } = JSON.parse(readFileSync(new URL('../landing/static/pricing.json', root), 'utf8'));
const count = (value) => (value === null ? 'No limit' : value.toLocaleString('en-US'));
const seconds = (value) => (value === null ? 'No limit' : `${value} s`);
const gb = (value) => (value === null ? 'No limit' : `${value} GB`);
const yearly = (plan) => (plan.priceAnnualUsd === null ? '—' : `$${plan.priceAnnualUsd}`);
const rates = metering.overageRates;
const storageHome = "in the database's home region";
const storageReplica = "Replica storage is not billed separately";
if (!metering.storage.definition.includes(storageHome) || !metering.storage.definition.includes(storageReplica)) {
  throw new Error("pricing catalog storage definition no longer contains the sentences this page rewrites");
}
const storageDefinition = metering.storage.definition
  .replace(storageHome, "of one copy of the database")
  .replace(storageReplica, "Copies in the database's other regions are not billed separately");
// Scan budgets are not in the catalog: TerraBase sets them per plan in
// src/TerraBase/Features/Qos/QosPolicy.cs (ScanRows, ScanBytes).
const scanRows = { free: '10,000', starter: '50,000', pro: '100,000', team: '100,000', enterprise: 'No limit' };
const scanBytes = { free: '16 MiB', starter: '64 MiB', pro: '128 MiB', team: '128 MiB', enterprise: 'No limit' };
const outputs = {
  'docs/pricing.md': `# Pricing

Prices and limits come from the [published catalog](https://terrascale.tech/pricing.json).

| Plan | Monthly price (USD) | Yearly price (USD) | Included operation units | Write cap | SQL read fair use | Client egress | Storage |
| --- | --- | --- | --- | --- | --- | --- | --- |
${plans.map((plan) => `| ${plan.name} | $${plan.priceMonthlyUsd} | ${yearly(plan)} | ${count(plan.limits.operationUnitsPerMonth)} | ${count(plan.limits.operationUnitCapPerMonth)} | ${count(plan.limits.sqlReadUnitsPerMonth)} | ${gb(plan.limits.egressGbPerMonth)}/month | ${plan.limits.storageGb} GB |`).join('\n')}

Starter, Pro and Team can be billed monthly or yearly; a year costs ten monthly payments (two months free). A paid plan can only switch to a billing interval it is sold at; upgrades take effect immediately with a prorated charge and other changes apply at renewal.

## What is billed

- **Writes.** ${metering.operationUnit.definition}
- **Function invocations.** Each invocation is ${count(metering.functionInvocation.operationUnits)} operation unit${metering.functionInvocation.operationUnits === 1 ? '' : 's'}. ${metering.functionInvocation.definition}
- **Storage.** ${storageDefinition}
- **Reads.** SQL reads are not billed; they count toward the plan's monthly fair use. Shape reads served from the edge are free and not counted.

${metering.allowances.definition}

Overage rates on paid plans: $${rates.operationUnitsPerMillionUsd} per million operation units and $${rates.storageGbRegionMonthUsd} per GB-month of storage.

When a plan's write cap or SQL read fair use is reached, the statement answers \`429 quota_exceeded\` (Postgres SQLSTATE \`53100\`) until the next UTC month or an upgrade. A function invocation past the write cap answers \`429 quota_exceeded\` the same way.

## Regions per database

| Plan | Metered regions per database |
| --- | --- |
${plans.map((plan) => `| ${plan.name} | ${count(plan.entitlements.regionsPerDatabase)} |`).join('\n')}

A database lives in a set of regions, at least one. Every region of the set is an equal peer that accepts reads and writes; there is no home region.
The catalog's regionsPerDatabase counts the regions in which a database's writes and stored bytes are metered: one per database on every plan. Copies in the database's other regions are not billed separately. See [regions](/concepts/regions) and [replication](/concepts/replication).

## Spending caps

Plan write caps are enforced. Customer-set hard dollar spending caps are coming. Internal report-only spending
thresholds do not stop services or cap a customer's bill.

See [support](/legal/support) for support targets.
`,
  'docs/limits.md': `# Limits

These are the limits a request or a plan can hit. All SQL surfaces (HTTP, WebSocket, Postgres wire) share them and answer with the same error codes and SQLSTATEs. See [SQL](/concepts/sql) for the SQL model and [errors](/guides/errors) for retry behavior.

## SQL statements

| Limit | Value | Refusal |
| --- | --- | --- |
| Statements per request or \`execute\` frame | 1 to 256 | \`invalid_request\` (400) before anything runs |
| Statement text | 16 KiB (16,384 UTF-8 bytes) | \`sql_error\` (400) / SQLSTATE 0A000 |
| HTTP request body | 1 MiB | \`too_large\` (413) / 54000 |
| WebSocket frame | 1 MiB (the first auth frame 16 KiB) | close 1009 |
| Result size | 10,000 rows and 4 MiB | \`too_large\` (400) / 54000 |
| Rows changed per transaction | 1,024 | \`too_large\` (400) / 54000 |
| Database file growth per transaction | 8 MiB | \`too_large\` (400) / 54000 |
| Any value, string or row a statement builds | 16 MiB | \`too_large\` (400) / 54000 |
| Expression nesting | 200 levels | \`sql_error\` (400) / 54001 |
| Terms in a compound \`SELECT\` | 50 | \`sql_error\` (400) / 54001 |
| \`LIKE\` or \`GLOB\` pattern | 1,000 bytes | \`sql_error\` (400) / 54001 |
| Trigger and foreign-key cascade nesting | 32 levels | \`sql_error\` (400) / 54001 |
| Function arguments | 127 | \`sql_error\` (400) / 54023 |
| Bound parameter number | \`?8192\` | \`sql_error\` (400) / 54023 |

A statement that needs more memory than the server can spare, including one that compiles to an oversized program, answers \`storage_busy\` (503) / 53200; retry it, and simplify it if it keeps failing.

A query that stays inside result limits but scans or works past the plan's scan budget answers \`query_canceled\` (400) / 57014 with \`query too expensive: it exceeds the work budget\`; add indexes or a \`LIMIT\`, or narrow the query. A request whose statements together spend more than 5 s executing answers \`query_canceled\` (400) / 57014 with \`the request exceeded its 5 s time limit; split the batch or narrow the query\`. Only statement and commit execution counts toward the 5 s; time spent admitted, queued or waiting on the organization's limits does not.

## Transactions

| Surface | Transaction limit |
| --- | --- |
| HTTP | One request is one atomic batch, all or nothing. \`BEGIN STRONG\` … \`COMMIT\` may wrap the whole batch; other \`BEGIN\`, \`COMMIT\` and \`ROLLBACK\` are refused. |
| WebSocket | \`begin\`/\`commit\`/\`rollback\` frames. At most 5 seconds, then it rolls back. |
| Postgres wire | \`BEGIN\`/\`COMMIT\`/\`ROLLBACK\` and savepoints. At most 5 seconds, then it rolls back. |

A transaction left open past the limit rolls back and answers \`transaction_rolled_back\` (400) / 40000.

## Postgres wire sessions

| Limit | Value | On exceed |
| --- | --- | --- |
| Startup | 10 seconds to finish the handshake, packets at most 10,000 bytes | connection closed (\`08P01\` on a bad packet) |
| Message size | 1 MiB per protocol message | connection closed (\`08P01\`) |
| Prepared statements | 256 per session | \`54000\` |
| Portals | 256 per session, holding at most 16 MiB of parameters and pending rows together | \`54000\` |
| \`LISTEN\` channels | 64 per session | \`54000\` |
| \`numeric\` values | 1,000 digits before and 1,000 after the decimal point | \`22003\` on results, \`22P03\` on binary parameters |
| Extensions, \`NOTIFY\`, \`CREATE SEQUENCE\`, two-phase commit | not supported | \`0A000\` |

## Requests per organization

| Limit | Free | Starter | Pro | Team | Enterprise |
| --- | --- | --- | --- | --- | --- |
| Requests per second | 50 | 200 | 500 | 500 | No limit |
| Burst requests | 100 | 400 | 1,000 | 1,000 | No limit |
| Concurrent requests | 4 | 8 | 16 | 16 | No limit |
| Query timeout | 1 s | 2 s | 5 s | 5 s | No limit |
| Scan budget, candidate rows | 10,000 | 50,000 | 100,000 | 100,000 | No limit |
| Scan budget, bytes scanned | 16 MiB | 64 MiB | 128 MiB | 128 MiB | No limit |
| Live databases | 1 | 5 | 25 | 100 | No limit |
| Storage ceiling | 0.1 GB | 5 GB | 100 GB | 100 GB | No limit |
| Point-in-time retention | 1 day | 3 days | 7 days | 14 days | 30 days |

Beyond the request, burst or concurrency limit, HTTP answers \`429 tenant_busy\` (SQLSTATE \`53300\`) with a \`Retry-After\` header; the Postgres wire answers SQLSTATE \`53300\`. Past the storage ceiling a write answers \`429 quota_exceeded\` (53100) over SQL and \`429 storage_quota\` over file storage; neither sends \`Retry-After\`, and the file-storage message names the used and allowed gigabytes and the next step. Past the database allowance a create answers \`409 database_limit\`. A database's point-in-time restore window reaches back at most its plan's retention; see [backups and restore](/guides/backups-and-restore).

## Monthly operation units and SQL read fair use

| Plan | Operation units included | Write cap | SQL read fair use |
| --- | --- | --- | --- |
${plans.map((plan) => `| ${plan.name} | ${count(plan.limits.operationUnitsPerMonth)} | ${count(plan.limits.operationUnitCapPerMonth)} | ${count(plan.limits.sqlReadUnitsPerMonth)} |`).join('\n')}

One unit is each started 4 KiB. At the cap (the allowance on Free) new writes, and at fair use new SQL reads, answer \`429 quota_exceeded\` (SQLSTATE \`53100\`) until the next UTC month or an upgrade. Shape reads stay available. See [pricing](/pricing) for billing and overage.
`,
};
for (const [path, text] of Object.entries(outputs)) {
  const output = new URL(path, root);
  if (process.argv.includes('--check')) {
    if (readFileSync(output, 'utf8') !== text) throw new Error(`Generated page is stale: ${path}`);
  } else {
    writeFileSync(output, text);
  }
}
