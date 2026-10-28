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
// Paid plans list every interval they are sold at; Free needs no card and Enterprise is arranged through sales.
const subscription = (p) => {
  if (p.priceMonthlyUsd === 0) return `${usd(p.priceMonthlyUsd)}; no card`;
  if (!p.available) return `${usd(p.priceMonthlyUsd)}/month support + usage; contact sales`;
  return p.billingIntervals.map((interval) => {
    switch (interval) {
      case "month": return `${usd(p.priceMonthlyUsd)}/month`;
      case "year": return `${usd(p.priceAnnualUsd)}/year`;
      default: throw new Error(`Unknown billing interval: ${interval}`);
    }
  }).join(" or ");
};
// Months of the monthly price that yearly billing saves; every paid plan must save the same.
const savedMonths = [...new Set(plans.filter((p) => p.billingIntervals.includes("year")).map((p) => 12 - p.priceAnnualUsd / p.priceMonthlyUsd))];
if (savedMonths.length !== 1 || !Number.isInteger(savedMonths[0]) || savedMonths[0] <= 0) throw new Error("Paid plans must save the same whole number of months on yearly billing");
const regionList = regions.map((r) => `${r.id.toUpperCase()} (${r.name})`).join(", ");

const pricingMd = `${header}
# Pricing

> **Approved subscription prices, allowances and usage rates.** Prices come from the catalog at
> [terrascale.tech/pricing.json](https://terrascale.tech/pricing.json); the
> [pricing page](https://terrascale.tech/pricing) renders the same catalog.
> All prices are in ${catalog.currency}. Free needs only sign-up; Starter, Pro and Team are bought through
> checkout; Enterprise is arranged through sales.

## How usage is metered

- **${metering.operationUnit.name}s.** ${metering.operationUnit.definition} ${metering.operationUnit.rules.join(" ")}
- **${metering.storage.name}.** ${metering.storage.definition} Storage uses decimal GB-region-month, averaged over time during the month.
- **${metering.egress.name}.** ${metering.egress.definition}
- **Allowances.** ${metering.allowances.definition}

## Plans and allowances

${table("")}
${row("Subscription", subscription)}
${row("Availability", (p) => !p.available ? "Contact sales" : p.priceMonthlyUsd === 0 ? "Sign up" : "Checkout")}
${row("Projects", (p) => limit(p.limits.projects))}
${row("Databases", (p) => limit(p.limits.databases))}
${row("Members", (p) => limit(p.limits.members))}
${row("Storage (GB-region-month)", (p) => limit(p.limits.storageGb, storage))}
${row("Operation units / month", (p) => limit(p.limits.operationUnitsPerMonth))}
${row("Client egress / month", (p) => limit(p.limits.egressGbPerMonth, storage))}
${row("Regions per database", () => `up to ${policy.maxRegions}`)}

Launch regions: ${regionList}.

Request rates, concurrency, query timeout, storage limit, point-in-time recovery, backup
retention and support response targets per plan are in [plan entitlements](./limits.md#plan-entitlements).

Starter, Pro and Team are billed monthly or yearly. A year costs ${12 - savedMonths[0]} monthly payments, so yearly billing saves
${savedMonths[0]} months. No trials are offered.

## Pay-as-you-go rates

Paid plans include the allowances above. Operation units over the allowance are added to the next
subscription invoice at the rate below. Storage and client egress over the allowance are not billed yet.

| Meter | Rate (USD) |
| --- | --- |
| Operation units | ${usd(metering.overageRates.operationUnitsPerMillionUsd)} per million |
| Storage | ${usd(metering.overageRates.storageGbRegionMonthUsd)} per GB-region-month |
| Client egress | ${usd(metering.overageRates.clientEgressGbUsd)} per decimal GB |

**Shared organization allowances.** ${policy.sharedAllowances}

**Free.** ${policy.freeEligibility}

**Enterprise.** ${plans.find((p) => p.id === "enterprise").billing} ${policy.enterpriseNotes}

${Object.entries(policy.billingPolicies).map(([title, body]) => `## ${title}\n\n${body}`).join("\n\n")}

See also: [Limits](./limits.md).
`;

const ceiling = (value, format = number) => (value === null ? "No plan ceiling" : format(value));
const days = (n) => `${n} ${n === 1 ? "day" : "days"}`;

const limitsMd = `${header}
# Limits

## Documents API

The current project-scoped contract has a 4 MiB request-body limit, a 1 MiB
document limit, a query page limit of 1–1000 items (default 50), at most 4 sort
keys and 64 projection paths. A query page also stops at 8 MiB of document JSON
or its scan budget, so an empty page can still have a continuation cursor.
Follow cursors until exhaustion; see [queries and pagination](/guides/queries).
The [change stream](/guides/realtime) keeps events for 7 days.

## Plan entitlements

${policy.entitlementsNote}

${table("Entitlement (per organization)")}
${row("Requests/s (burst)", (p) => `${ceiling(p.entitlements.requestsPerSecond)} (${ceiling(p.entitlements.burstRequests)})`.replace("No plan ceiling (No plan ceiling)", "No plan ceiling"))}
${row("Concurrent HTTP requests", (p) => ceiling(p.entitlements.concurrentRequests))}
${row("Query execution time", (p) => ceiling(p.entitlements.queryTimeoutSeconds, (n) => `${n} s`))}
${row("Storage limit", (p) => ceiling(p.entitlements.storageLimitGb, storage))}
${row("Point-in-time recovery window", (p) => days(p.entitlements.pitrDays))}
${row("Backup retention", (p) => days(p.entitlements.backupRetentionDays))}
${row("Regions per database", (p) => number(p.entitlements.regionsPerDatabase))}
${row("Support response target", (p) => p.entitlements.support)}
${row("Databases", (p) => limit(p.limits.databases))}

Subscription prices, allowances and metering are on the [pricing page](./pricing.md).
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
