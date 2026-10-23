// Generates docs/pricing.md and docs/limits.md from the canonical plan catalog
// (data/canonical/pricing.json, vendored from https://terrascale.tech/pricing.json
// by scripts/canonical-data.mjs) plus docs-only policies in data/policies.json.
// Usage: node scripts/generate-pricing.mjs [--check]
// --check exits non-zero when the checked-in pages differ from generated output.
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const pricingPath = join(root, "docs", "pricing.md");
const limitsPath = join(root, "docs", "limits.md");
const check = process.argv.includes("--check");

const catalog = JSON.parse(readFileSync(join(root, "data", "canonical", "pricing.json"), "utf8"));
const regions = JSON.parse(readFileSync(join(root, "data", "canonical", "regions.json"), "utf8")).regions;
const policy = JSON.parse(readFileSync(join(root, "data", "policies.json"), "utf8"));
const { plans, metering } = catalog;
const planNames = plans.map((p) => p.name);
const header = "<!-- GENERATED from data/canonical/pricing.json (https://terrascale.tech/pricing.json) and data/policies.json by scripts/generate-pricing.mjs — do not edit. -->";

const number = (n) => n.toLocaleString("en-US");
const usd = (n) => `$${n.toLocaleString("en-US", { minimumFractionDigits: Number.isInteger(n) ? 0 : 2, maximumFractionDigits: 2 })}`;
const storage = (gb) => (gb < 1 ? `${number(Math.round(gb * 1000))} MB` : `${number(gb)} GB`);
const limit = (value, format = number) => (value === null ? "Custom" : format(value));
const row = (label, cell) => `| ${label} | ${plans.map((p) => cell(p)).join(" | ")} |`;
const table = (label) => `| ${label} | ${planNames.join(" | ")} |\n| --- | ${planNames.map(() => "---").join(" | ")} |`;
const subscription = (p) => {
  switch (p.billingInterval) {
    case "free": return `${usd(p.priceMonthlyUsd)}; no card`;
    case "year": return `${usd(p.priceAnnualUsd)}/year only (${usd(p.priceMonthlyUsd)}/month equivalent)`;
    case "month": return `${usd(p.priceMonthlyUsd)}/month`;
    case "contact": return `${usd(p.priceMonthlyUsd)}/month support + usage; contact sales`;
    default: throw new Error(`Unknown billing interval: ${p.billingInterval}`);
  }
};
const regionList = regions.map((r) => `${r.id.toUpperCase()} (${r.name})`).join(", ");

const pricingMd = `${header}
# Pricing

> **Approved subscription prices; proposed allowances and usage rates.** Prices come from the catalog at
> [terrascale.tech/pricing.json](https://terrascale.tech/pricing.json); the
> [pricing page](https://terrascale.tech/pricing) renders the same catalog.
> All prices are in ${catalog.currency}. Allowances and overage rates remain pending review.
> Usage charges are not yet collected. Confirm enrollment and service availability before purchase;
> a published price does not establish that checkout or billing is available.

## How usage is metered

- **${metering.operationUnit.name}s.** ${metering.operationUnit.definition} ${metering.operationUnit.rules.join(" ")}
- **${metering.storage.name}.** ${metering.storage.definition} Storage uses decimal GB-region-month, averaged over time during the month.
- **${metering.egress.name}.** ${metering.egress.definition}
- **Allowances.** ${metering.allowances.definition}

## Plans and proposed allowances

${table("")}
${row("Subscription", subscription)}
${row("Availability", (p) => p.available ? "Public Alpha; confirm enrollment" : "Contact sales; no self-service purchase")}
${row("Projects", (p) => limit(p.limits.projects))}
${row("Databases", (p) => limit(p.limits.databases))}
${row("Members", (p) => limit(p.limits.members))}
${row("Proposed storage (GB-region-month)", (p) => limit(p.limits.storageGb, storage))}
${row("Proposed operation units / month", (p) => limit(p.limits.operationUnitsPerMonth))}
${row("Proposed client egress / month", (p) => limit(p.limits.egressGbPerMonth, storage))}
${row("Regions per database", () => `up to ${policy.maxRegions}`)}

Launch regions: ${regionList}.

No trials or annual Pro/Team discounts are offered.

## Proposed pay-as-you-go rates

Paid plans include the proposed allowances above, then proposed pay-as-you-go usage:

| Meter | Proposed rate (USD) |
| --- | --- |
| Operation units | ${usd(metering.overageRates.operationUnitsPerMillionUsd)} per million |
| Storage | ${usd(metering.overageRates.storageGbRegionMonthUsd)} per GB-region-month |
| Client egress | ${usd(metering.overageRates.clientEgressGbUsd)} per decimal GB |

These allowances and rates are pending review. Usage charges are not yet collected.

**Shared organization allowances.** ${policy.sharedAllowances}

**Free.** ${policy.freeEligibility}

**Enterprise.** ${plans.find((p) => p.id === "enterprise").billing} ${policy.enterpriseNotes}

${Object.entries(policy.billingPolicies).map(([title, body]) => `## ${title}\n\n${body}`).join("\n\n")}

See also: [Limits](./limits.md).
`;

const e = policy.engineLimits;

const limitsMd = `${header}
# Limits

## Documents API

The current project-scoped contract has a 4 MiB request-body limit, a 1 MiB
document limit, a query page limit of 1–1000 items (default 50), at most 4 sort
keys and 64 projection paths. A query page also stops at 8 MiB of document JSON
or its scan budget, so an empty page can still have a continuation cursor.
Follow cursors until exhaustion; see [queries and pagination](/guides/queries).
The [change stream](/guides/realtime) keeps events for 7 days.

## Engine safety tiers

${e._note}

| Engine limit (per organization) | ${e.tiers.join(" | ")} |
| --- | ${e.tiers.map(() => "---").join(" | ")} |
${e.rows.map((r) => `| ${r.join(" | ")} |`).join("\n")}

## Plan policies

The existing engine PITR defaults (days) are ${Object.entries(policy.enginePitrRetentionDays).map(([id, days]) => `${id} ${days}`).join(" / ")}. These engine defaults do not publish PITR entitlements for the commercial plans. No commercial plan-to-engine-tier mapping is published.

Proposed databases per org: ${plans.map((p) => `${p.name} ${limit(p.limits.databases)}`).join(" / ")}.

Subscription prices, proposed allowances and metering are on the [pricing page](./pricing.md). Usage charges are not yet collected.
`;

if (check) {
  let ok = true;
  for (const [path, expected] of [[pricingPath, pricingMd], [limitsPath, limitsMd]]) {
    if (readFileSync(path, "utf8") !== expected) {
      console.error(`FAIL: ${path.slice(root.length + 1)} is stale; run node scripts/generate-pricing.mjs`);
      ok = false;
    }
  }
  if (!ok) process.exit(1);
  console.log("pricing/limits pages are up to date.");
} else {
  writeFileSync(pricingPath, pricingMd);
  writeFileSync(limitsPath, limitsMd);
  console.log("wrote docs/pricing.md docs/limits.md");
}
