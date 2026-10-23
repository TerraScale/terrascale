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
const usd = (n) => `$${number(n)}`;
const storage = (gb) => (gb < 1 ? `${number(Math.round(gb * 1000))} MB` : `${number(gb)} GB`);
const limit = (value, format = number) => (value === null ? "Custom" : format(value));
const row = (label, cell) => `| ${label} | ${plans.map((p) => cell(p)).join(" | ")} |`;
const table = (label) => `| ${label} | ${planNames.join(" | ")} |\n| --- | ${planNames.map(() => "---").join(" | ")} |`;
const pitr = (id) => (policy.pitrRetentionDays[id] === undefined ? "Not published" : `${policy.pitrRetentionDays[id]} day${policy.pitrRetentionDays[id] === 1 ? "" : "s"}`);
const regionList = regions.map((r) => `${r.id.toUpperCase()} (${r.name})`).join(", ");

const pricingMd = `${header}
# Pricing

> **Pre-launch policy.** Public Alpha prices and allowances come from the canonical catalog at
> [terrascale.tech/pricing.json](https://terrascale.tech/pricing.json); the
> [pricing page](https://terrascale.tech/pricing) renders the same catalog.
> All prices are in ${catalog.currency}. Confirm approved enrollment terms and
> serving-release availability; this catalog does not qualify a workflow.

## How usage is metered

- **${metering.operationUnit.name}s.** ${metering.operationUnit.definition} ${metering.operationUnit.rules.join(" ")}
- **${metering.storage.name}.** ${metering.storage.definition}
- **Allowances.** ${metering.allowances.definition}

## Plans

${table("")}
${row("Monthly price", (p) => usd(p.priceMonthlyUsd))}
${row("Annual price", (p) => usd(p.priceAnnualUsd))}
${row("Projects", (p) => limit(p.limits.projects))}
${row("Databases", (p) => limit(p.limits.databases))}
${row("Members", (p) => limit(p.limits.members))}
${row("Storage across regions", (p) => limit(p.limits.storageGb, storage))}
${row("Operation units / month", (p) => limit(p.limits.operationUnitsPerMonth))}
${row("Regions per database", () => `up to ${policy.maxRegions}`)}
${row("PITR retention", (p) => pitr(p.id))}

Launch regions: ${regionList}.

${plans.filter((p) => p.trialDays).map((p) => p.name).join(" and ")} include one eligible ${plans.find((p) => p.trialDays)?.trialDays}-day trial per verified payment method and organization.

**Shared organization allowances.** ${policy.sharedAllowances}

**Hobby.** ${policy.hobbyEligibility}

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

## Per-organization limits

${e._note}

| Limit (per organization) | ${e.tiers.join(" | ")} |
| --- | ${e.tiers.map(() => "---").join(" | ")} |
${e.rows.map((r) => `| ${r.join(" | ")} |`).join("\n")}

## Plan policies

PITR retention (days): ${Object.entries(policy.pitrRetentionDays).map(([id, days]) => `${plans.find((p) => p.id === id).name} ${days}`).join(" / ")}.

Databases per org: ${plans.map((p) => `${p.name} ${limit(p.limits.databases)}`).join(" / ")}.

Plan allowances and operation-unit metering are on the [pricing page](./pricing.md).
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
