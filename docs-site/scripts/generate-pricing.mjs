import { readFileSync, writeFileSync } from 'node:fs';

const root = new URL('../', import.meta.url);
const { plans, metering } = JSON.parse(readFileSync(new URL('data/canonical/pricing.json', root), 'utf8'));
const count = (value) => (value === null ? 'No limit' : value.toLocaleString('en-US'));
const rates = metering.overageRates;
const outputs = {
  'docs/pricing.md': `# Pricing

Prices and limits come from the [published catalog](https://terrascale.tech/pricing.json).

| Plan | Monthly price (USD) | Included write units | Write cap | SQL read fair use | Storage |
| --- | --- | --- | --- | --- | --- |
${plans.map((plan) => `| ${plan.name} | $${plan.priceMonthlyUsd} | ${count(plan.limits.operationUnitsPerMonth)} | ${count(plan.limits.operationUnitCapPerMonth)} | ${count(plan.limits.sqlReadUnitsPerMonth)} | ${plan.limits.storageGb} GB |`).join('\n')}

## What is billed

- **Writes.** ${metering.operationUnit.definition}
- **Storage.** ${metering.storage.definition}
- **Reads.** SQL reads are not billed; they count toward the plan's monthly fair use. Shape reads served from the edge are free and not counted.

${metering.allowances.definition}

Overage rates on paid plans: $${rates.operationUnitsPerMillionUsd} per million write units and $${rates.storageGbRegionMonthUsd} per GB-month of storage.

When a plan's write cap or SQL read fair use is reached, the statement answers \`429 quota_exceeded\` (Postgres SQLSTATE \`53400\`) until the next UTC month or an upgrade.

See [support](/legal/support) for support targets.
`,
  'docs/limits.md': `# Limits

SQL limits and recovery guarantees are being updated. See the
[SQL overview](/concepts/sql) and [support](/legal/support).
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
