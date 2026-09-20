import { spawnSync } from "node:child_process";

const result = spawnSync("npm", ["audit", "--json", "--audit-level=high"], {
  encoding: "utf8",
  stdio: ["ignore", "pipe", "pipe"],
});

if (result.error) {
  console.error(`Dependency audit could not run: ${result.error.message}`);
  process.exit(1);
}

let report;
try {
  report = JSON.parse(result.stdout);
} catch (error) {
  console.error("Dependency audit did not return valid JSON.");
  if (result.stderr.trim()) console.error(result.stderr.trim());
  process.exit(1);
}

const vulnerabilities = report.metadata?.vulnerabilities ?? {};
const high = Number(vulnerabilities.high ?? 0);
const critical = Number(vulnerabilities.critical ?? 0);
const total = Number(vulnerabilities.total ?? 0);

console.log(
  `Dependency audit: ${total} total vulnerabilities (${high} high, ${critical} critical).`,
);

if (high > 0 || critical > 0 || result.status !== 0) {
  console.error(
    "Dependency audit failed: high or critical findings require documented disposition before release.",
  );
  process.exit(1);
}
