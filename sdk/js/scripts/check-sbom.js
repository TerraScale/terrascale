#!/usr/bin/env node

import { createHash } from "node:crypto";
import { readFile, writeFile } from "node:fs/promises";
import { spawnSync } from "node:child_process";
import { resolve } from "node:path";

const output = process.argv[2] === "--output" ? process.argv[3] : undefined;
if (process.argv.length > 2 && (process.argv.length !== 4 || process.argv[2] !== "--output" || output === undefined)) {
  throw new Error("usage: check-sbom.js [--output PATH]");
}

const packageJson = JSON.parse(await readFile("package.json", "utf8"));
const lockBytes = await readFile("package-lock.json");
const lockfile = JSON.parse(lockBytes.toString("utf8"));
if (lockfile.packages?.[""]?.name !== packageJson.name || lockfile.packages?.[""]?.version !== packageJson.version) {
  throw new Error("package-lock.json root does not match package.json");
}
const npmExecutable = process.env.npm_execpath;
/** @type {[string, string[]]} */
const command = npmExecutable === undefined
  ? ["npm", ["sbom", "--package-lock-only", "--sbom-format", "cyclonedx"]]
  : [process.execPath, [npmExecutable, "sbom", "--package-lock-only", "--sbom-format", "cyclonedx"]];
const result = spawnSync(command[0], command[1], { encoding: "utf8" });
if (result.status !== 0) throw new Error(result.stderr || "npm sbom failed");
const sbom = JSON.parse(result.stdout);
if (sbom.bomFormat !== "CycloneDX" || sbom.specVersion !== "1.5" || !Array.isArray(sbom.components)) {
  throw new Error("SBOM must be CycloneDX 1.5 with a components array");
}
const root = sbom.metadata?.component;
if (typeof root?.name !== "string" || root?.version !== packageJson.version || root?.type !== "library") {
  throw new Error("SBOM root version or type does not match package.json");
}
const refs = new Set();
for (const component of sbom.components) {
  if (component?.type !== "library" || typeof component.name !== "string" || typeof component.version !== "string" ||
      typeof component["bom-ref"] !== "string" || typeof component.purl !== "string" || !component.purl.startsWith("pkg:npm/")) {
    throw new Error("SBOM contains an invalid npm component");
  }
  if (refs.has(component["bom-ref"])) throw new Error(`SBOM contains duplicate component ${component["bom-ref"]}`);
  refs.add(component["bom-ref"]);
}
const evidence = {
  schemaVersion: 1,
  mediaType: "application/vnd.cyclonedx+json;version=1.5",
  package: { name: packageJson.name, version: packageJson.version },
  packageLockSha256: createHash("sha256").update(lockBytes).digest("hex"),
  sbom,
};
const serialized = `${JSON.stringify(evidence, null, 2)}\n`;
if (output !== undefined) await writeFile(resolve(output), serialized, { mode: 0o644 });
console.log(`SBOM OK: ${packageJson.name}@${packageJson.version} (${refs.size} components, lock ${evidence.packageLockSha256})`);
