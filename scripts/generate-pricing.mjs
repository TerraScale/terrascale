import { readFileSync, writeFileSync } from 'node:fs';

const root = new URL('../', import.meta.url);
const { plans } = JSON.parse(readFileSync(new URL('data/canonical/pricing.json', root), 'utf8'));
const outputs = {
  'docs/pricing.md': `# Pricing

The SQL pricing guide is being updated. Subscription prices in the current
[published catalog](https://terrascale.tech/pricing.json) are:

| Plan | Monthly price (USD) |
| --- | --- |
${plans.map((plan) => `| ${plan.name} | $${plan.priceMonthlyUsd} |`).join('\n')}

See [support](/legal/support) for support targets. SQL usage accounting will be
published with the SQL product contract.
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
