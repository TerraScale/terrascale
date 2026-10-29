import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";
import { npmReleasePolicy } from "./npm-release-policy.js";
import { requireExactKeys } from "./strict-json.js";

/** @param {unknown} dependencies */
export function requireRuntimeDependencyPins(dependencies) {
  const pins = /** @type {Record<string, unknown>} */ (requireExactKeys(
    dependencies,
    Object.keys(npmReleasePolicy.runtimeDependencies),
    "Qualified runtime dependency pins",
  ));
  for (const [name, policy] of Object.entries(npmReleasePolicy.runtimeDependencies)) {
    if (pins[name] !== policy.version)
      throw new Error(`Runtime dependency ${name} requires exact version ${policy.version}.`);
  }
}

/**
 * @param {unknown} evidence
 * @param {unknown} dependencies
 */
export function requireRuntimeDependencyEvidence(evidence, dependencies) {
  requireRuntimeDependencyPins(dependencies);
  const receipt = /** @type {{count: unknown, packages: Record<string, unknown>}} */ (
    requireExactKeys(evidence, ["count", "packages"], "Runtime dependency evidence")
  );
  const policies = Object.entries(npmReleasePolicy.runtimeDependencies);
  if (receipt.count !== policies.length)
    throw new Error("Runtime dependency evidence has an invalid count.");
  const packages = requireExactKeys(receipt.packages, policies.map(([name]) => name), "Qualified runtime dependencies");
  for (const [name, policy] of policies) {
    const installed = /** @type {Record<string, unknown>} */ (
      requireExactKeys(packages[name], ["version", "license", "fileSha256"], `${name} license evidence`)
    );
    for (const [field, expected] of Object.entries(policy)) {
      if (installed[field] !== expected)
        throw new Error(`Runtime dependency ${name} has unqualified ${field}.`);
    }
  }
}

/**
 * @param {string} consumer
 * @param {unknown} dependencies
 */
export async function qualifyRuntimeDependencies(consumer, dependencies) {
  requireRuntimeDependencyPins(dependencies);
  const resolve = createRequire(path.join(consumer, "package.json"));
  /** @type {Record<string, {version: string, license: string, fileSha256: string}>} */
  const packages = {};
  for (const name of Object.keys(npmReleasePolicy.runtimeDependencies)) {
    const manifestPath = resolve.resolve(`${name}/package.json`);
    const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
    if (manifest.name !== name || Object.keys(manifest.dependencies ?? {}).length !== 0 ||
        Object.keys(manifest.optionalDependencies ?? {}).length !== 0 ||
        Object.keys(manifest.peerDependencies ?? {}).length !== 0)
      throw new Error(`Runtime dependency ${name} has unqualified transitive dependencies or identity.`);
    const license = await readFile(path.join(path.dirname(manifestPath), "LICENSE"));
    packages[name] = {
      version: manifest.version,
      license: manifest.license,
      fileSha256: createHash("sha256").update(license).digest("hex"),
    };
  }
  const evidence = { count: Object.keys(packages).length, packages };
  requireRuntimeDependencyEvidence(evidence, dependencies);
  return evidence;
}
