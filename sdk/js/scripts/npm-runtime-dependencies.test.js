import assert from "node:assert/strict";
import { mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "vitest";
import { qualifyRuntimeDependencies, requireRuntimeDependencyEvidence, requireRuntimeDependencyPins } from "./npm-runtime-dependencies.js";

const root = fileURLToPath(new URL("../", import.meta.url));
const pins = { ulid: "3.0.2" };

test("runtime qualification admits the installed pinned MIT ULID and rejects changed pins or receipts", async () => {
  const evidence = await qualifyRuntimeDependencies(root, pins);
  assert.equal(evidence.count, 1);
  assert.equal(evidence.packages.ulid?.license, "MIT");
  requireRuntimeDependencyEvidence(evidence, pins);
  for (const invalid of [{}, { ulid: "^3.0.2" }, { ulid: "3.0.1" }, { ...pins, unexpected: "1.0.0" }])
    assert.throws(() => requireRuntimeDependencyPins(invalid));
  for (const invalid of [
    { ...evidence, count: 0 },
    { ...evidence, packages: {} },
    { ...evidence, packages: { ...evidence.packages, unexpected: {} } },
    ...[{ version: "3.0.1" }, { license: "ISC" }, { fileSha256: "0".repeat(64) }, { dependencies: {} }]
      .map((change) => ({ ...evidence, packages: { ulid: { ...evidence.packages.ulid, ...change } } })),
  ]) assert.throws(() => requireRuntimeDependencyEvidence(invalid, pins));
});

test("installed dependency qualification verifies license bytes and refuses transitives", async () => {
  const consumer = await mkdtemp(path.join(tmpdir(), "ts754-runtime-license-"));
  try {
    const dependency = path.join(consumer, "node_modules", "ulid");
    await mkdir(dependency, { recursive: true });
    const originalManifest = JSON.parse(await readFile(path.join(root, "node_modules/ulid/package.json"), "utf8"));
    const license = await readFile(path.join(root, "node_modules/ulid/LICENSE"));
    await writeFile(path.join(dependency, "package.json"), JSON.stringify(originalManifest));
    await writeFile(path.join(dependency, "LICENSE"), license);
    await qualifyRuntimeDependencies(consumer, pins);
    await writeFile(path.join(dependency, "LICENSE"), "MIT\nsubstituted license");
    await assert.rejects(qualifyRuntimeDependencies(consumer, pins), /unqualified fileSha256/u);
    await writeFile(path.join(dependency, "LICENSE"), license);
    for (const change of [{ version: "3.0.1" }, { license: "ISC" }, { name: "unexpected" },
      { dependencies: { unexpected: "1.0.0" } }, { optionalDependencies: { unexpected: "1.0.0" } },
      { peerDependencies: { unexpected: "1.0.0" } },
      { peerDependencies: { unexpected: "1.0.0" }, peerDependenciesMeta: { unexpected: { optional: true } } }]) {
      await writeFile(path.join(dependency, "package.json"), JSON.stringify({ ...originalManifest, ...change }));
      await assert.rejects(qualifyRuntimeDependencies(consumer, pins), /unqualified/u);
    }
  } finally {
    await rm(consumer, { recursive: true, force: true });
  }
});
