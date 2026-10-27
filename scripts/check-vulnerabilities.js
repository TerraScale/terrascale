import { spawnSync } from "node:child_process";
import { dirname } from "node:path";

const inventory = spawnSync("git", ["ls-files", "-z", "--", "package-lock.json", "**/package-lock.json"], {
  encoding: "utf8",
});
if (inventory.error || inventory.status !== 0 || !inventory.stdout) {
  console.error("Could not inventory committed npm lockfiles.");
  process.exit(1);
}

for (const lockfile of inventory.stdout.split("\0").filter(Boolean)) {
  console.log(`Auditing ${lockfile}`);
  const result = spawnSync("npm", ["audit", "--package-lock-only", "--ignore-scripts", "--json", "--audit-level=high"], {
    cwd: dirname(lockfile),
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
    maxBuffer: 16 * 1024 * 1024,
  });

  if (result.error || result.signal) {
    console.error(`Dependency audit could not complete: ${result.error?.message ?? result.signal}`);
    process.exit(1);
  }

  let report;
  try {
    report = JSON.parse(result.stdout);
  } catch {
    console.error("Dependency audit did not return valid JSON.");
    if (result.stderr.trim()) console.error(result.stderr.trim());
    process.exit(1);
  }

  // Preserve every severity and reject incomplete/error responses, even on exit 0.
  console.log(result.stdout);
  if (result.stderr.trim()) console.error(result.stderr.trim());
  const counts = report.metadata?.vulnerabilities;
  if (report.auditReportVersion !== 2 || report.error || !counts ||
      !["info", "low", "moderate", "high", "critical", "total"].every(
        (key) => Number.isSafeInteger(counts[key]) && counts[key] >= 0,
      )) {
    console.error("Dependency audit returned an incomplete or error report.");
    process.exit(1);
  }

  if (counts.high > 0 || counts.critical > 0 || result.status !== 0) {
    console.error("Dependency audit failed; fix high/critical findings before advancing this tree.");
    process.exit(1);
  }
}
