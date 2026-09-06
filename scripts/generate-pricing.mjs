// Generates docs/pricing.md and docs/limits.md from data/pricing.json (L-1 draft).
// Usage: node scripts/generate-pricing.mjs [--check]
// --check exits non-zero when the checked-in pages differ from generated output.
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const dataPath = join(root, "data", "pricing.json");
const pricingPath = join(root, "docs", "pricing.md");
const limitsPath = join(root, "docs", "limits.md");
const check = process.argv.includes("--check");

const d = JSON.parse(readFileSync(dataPath, "utf8"));
const planNames = d.plans.map((p) => p.name);

function planRow(f) {
  return `| ${f} | ${d.plans.map((p) => p[f] ?? "—").join(" | ")} |`;
}

const pricingMd = `<!-- GENERATED from data/pricing.json by scripts/generate-pricing.mjs — do not edit. Draft; publication requires measured economics and owner approval. -->
# Pricing

> **DRAFT.** Publication awaits measured unit economics and owner approval
> of the final price sheet. These targets do not establish service readiness.
>
> **Pay for writes and storage; everything else is free.** Reads, queries,
> egress, CDN delivery, fan-out, subscribers, and webhook delivery are free
> and unbilled. Reads, queries, and direct member read egress are metered for
> abuse guards and cost attribution; a guard is never a charge. A write is billed once no matter how many views or
> subscribers it reaches. We never claim infrastructure is free:
> selected Regions multiply 4 KiB write units before included allowances,
> shown before Region selection.

## What you pay for

- **Writes.** ${d.writeUnit.definition} ${d.writeUnit.replication}
- **Storage.** ${d.storage.definition}
- Not billed as storage: ${d.storage.notBilled.join(", ")}.
- Free and unbilled: ${d.billingDimensions.freeUnbilled.join(", ")}.
- Metered for abuse guards and cost attribution (never billed): ${d.billingDimensions.meteredForGuards.join(", ")}.

## Plans

|  | ${planNames.join(" | ")} |
| --- | ${planNames.map(() => "---").join(" | ")} |
${planRow("price")}
${planRow("writes")}
${planRow("storage")}
${planRow("databases")}
${planRow("pitrDays")}

Overage and PAYG: **${d.overagePayg.writes}**, **${d.overagePayg.storage}** (${d.overagePayg.note})

**Shared organization allowances.** ${d.sharedAllowances}

**Free tier.** ${d.freeTier.databases} database, ${d.freeTier.storage}, **${d.freeTier.writesPerMonth.toLocaleString("en-US")} writes/month**, up to ${d.freeTier.maxRegions} regions. ${d.freeTier.notes}

**Scale.** ${d.plans.find((p) => p.name === "Scale").notes}

**Idle Free databases.** ${d.reaper}

${Object.entries(d.billingPolicies).map(([title, body]) => `## ${title}\n\n${body}`).join("\n\n")}

See also: [Limits](./limits.md).
`;

const g = d.limits.abuseGuards;
const guardRow = (label, obj) =>
  `| ${label} | ${planNames.map((p) => obj[p]).join(" | ")} |`;

const limitsMd = `<!-- GENERATED from data/pricing.json by scripts/generate-pricing.mjs — do not edit. Draft; publication requires measured economics and owner approval. -->
# Limits

> **DRAFT.** Publication awaits measured unit economics and owner approval.

PITR retention (days): Free ${d.limits.pitrRetentionDays.Free} / Starter ${d.limits.pitrRetentionDays.Starter} / Pro ${d.limits.pitrRetentionDays.Pro} / Scale ${d.limits.pitrRetentionDays.Scale}.

Databases per org: Free ${d.limits.databasesPerOrg.Free} / Starter ${d.limits.databasesPerOrg.Starter} / Pro ${d.limits.databasesPerOrg.Pro} / Scale ${d.limits.databasesPerOrg.Scale}.

- **Max document:** ${d.limits.maxDocument}.
- **History retention:** ${d.limits.historyRetention}
- **Idle Free databases:** ${d.reaper}

## Abuse guards (unbilled, enforced, no upsell UI)

${g._note} Reads, queries, and direct member read egress are metered for abuse guards and cost attribution;
exceeding one returns a typed error with the fix, and nothing is billed.

| Guard (per database) | ${planNames.join(" | ")} |
| --- | ${planNames.map(() => "---").join(" | ")} |
${guardRow("Read + query requests/s", g.readQueryRequestsPerSec)}
${guardRow("Direct member read bandwidth/month", g.directMemberReadBandwidthPerMonth)}
${guardRow("Distinct active shared views", g.distinctActiveSharedViews)}
${guardRow("Optional WebSocket connections", g.optionalWebSocketConnections)}
${guardRow("Webhook endpoints", g.webhookEndpoints)}

Subscribers per view are unlimited on every plan. CDN-delivered reactive data has no bandwidth guard.
`;

if (check) {
  const cur1 = readFileSync(pricingPath, "utf8");
  const cur2 = readFileSync(limitsPath, "utf8");
  let ok = true;
  if (cur1 !== pricingMd) {
    console.error("FAIL: docs/pricing.md is stale; run node scripts/generate-pricing.mjs");
    ok = false;
  }
  if (cur2 !== limitsMd) {
    console.error("FAIL: docs/limits.md is stale; run node scripts/generate-pricing.mjs");
    ok = false;
  }
  if (!ok) process.exit(1);
  console.log("pricing/limits pages are up to date.");
} else {
  writeFileSync(pricingPath, pricingMd);
  writeFileSync(limitsPath, limitsMd);
  console.log("wrote docs/pricing.md docs/limits.md");
}
