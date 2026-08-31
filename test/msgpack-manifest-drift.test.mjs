// Drift gate for the generated public `terrascale.sync.v2.msgpack` protocol
// The committed generated module
// must be byte-identical to what the pinned generator emits from the
// checked-in manifest, the manifest digest embedded in the generated header
// must match the committed manifest, and the generator must reject malformed
// manifests (duplicate tags, undeclared refs, bounds above the scan ceilings)
// instead of emitting anything.
//
// Run (imports the built SDK tree):
//
//   npm run build && node --test test/msgpack-manifest-drift.test.mjs

import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdtemp, readFile, readdir, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { describe, test } from "node:test";

const repositoryRoot = new URL("..", import.meta.url).pathname;
const generatorPath = path.join(repositoryRoot, "scripts", "generate-msgpack-protocol.mjs");
const manifestPath = path.join(
  repositoryRoot,
  "src",
  "msgpack",
  "manifest",
  "terrascale.sync.v2.msgpack.json",
);
const generatedPath = path.join(
  repositoryRoot,
  "src",
  "msgpack",
  "generated",
  "terrascale-sync-v2-msgpack.ts",
);

function runGenerator(...args) {
  return spawnSync(process.execPath, [generatorPath, ...args], { encoding: "utf8" });
}

describe("msgpack manifest drift", () => {
  test("regeneration is byte-identical to the committed generated module", async () => {
    const outDirectory = await mkdtemp(path.join(tmpdir(), "terrascale-msgpack-drift-"));
    const result = runGenerator("--out", outDirectory);
    assert.equal(result.status, 0, `generator failed: ${result.stderr}`);
    const [regenerated, committed] = await Promise.all([
      readFile(path.join(outDirectory, "terrascale-sync-v2-msgpack.ts"), "utf8"),
      readFile(generatedPath, "utf8"),
    ]);
    assert.equal(
      regenerated,
      committed,
      "the committed generated module differs from the generator output; run node scripts/generate-msgpack-protocol.mjs",
    );
  });

  test("--check mode passes against the committed files", () => {
    const result = runGenerator("--check");
    assert.equal(result.status, 0, `drift check failed: ${result.stdout}${result.stderr}`);
    assert.match(result.stdout, /drift check passed/u);
  });

  test("the generated header pins the committed manifest's SHA-256", async () => {
    const [manifestText, generated] = await Promise.all([
      readFile(manifestPath, "utf8"),
      readFile(generatedPath, "utf8"),
    ]);
    const digest = createHash("sha256").update(manifestText, "utf8").digest("hex");
    const exported = generated.match(/ManifestSha256 = "([0-9a-f]{64})"/u);
    assert.ok(exported !== null, "the generated module exports its manifest digest");
    assert.equal(exported[1], digest, "the generated module was built from a different manifest");
    const headerPinned = generated.match(/^\/\/ manifest sha256:\s+([0-9a-f]{64})$/mu);
    assert.ok(headerPinned !== null);
    assert.equal(headerPinned[1], digest);
  });

  test("the generated module carries the do-not-edit marker", async () => {
    const generated = await readFile(generatedPath, "utf8");
    assert.match(generated, /^\/\/ GENERATED FILE - DO NOT EDIT\./u);
  });

  test("the generator is deterministic across repeated runs", async () => {
    const first = await mkdtemp(path.join(tmpdir(), "terrascale-msgpack-drift-1-"));
    const second = await mkdtemp(path.join(tmpdir(), "terrascale-msgpack-drift-2-"));
    assert.equal(runGenerator("--out", first).status, 0);
    assert.equal(runGenerator("--out", second).status, 0);
    const [left, right] = await Promise.all([
      readFile(path.join(first, "terrascale-sync-v2-msgpack.ts"), "utf8"),
      readFile(path.join(second, "terrascale-sync-v2-msgpack.ts"), "utf8"),
    ]);
    assert.equal(left, right);
  });

  test("the generator emits exactly one file", async () => {
    const outDirectory = await mkdtemp(path.join(tmpdir(), "terrascale-msgpack-drift-"));
    assert.equal(runGenerator("--out", outDirectory).status, 0);
    const entries = await readdir(outDirectory);
    assert.deepEqual(entries, ["terrascale-sync-v2-msgpack.ts"]);
  });

  const malformedManifests = {
    "a duplicate message tag": (manifest) => {
      manifest.messages[1].tag = manifest.messages[0].tag;
    },
    "an undeclared type reference": (manifest) => {
      manifest.messages.find((entry) => entry.name === "snapshot").fields[1].type = { kind: "ref", name: "not_a_type" };
    },
    "a string bound above the scan ceiling": (manifest) => {
      manifest.messages.find((entry) => entry.name === "region_route_change").fields[0].type = { kind: "text", maxBytes: 262145 };
    },
    "an array bound above the scan ceiling": (manifest) => {
      manifest.messages.find((entry) => entry.name === "authorization_changed").fields[2].type = { kind: "array", maxItems: 10001, items: { kind: "text", maxBytes: 256 } };
    },
    "a binary bound above the scan ceiling": (manifest) => {
      manifest.messages.find((entry) => entry.name === "subscribe").fields[2].type = { kind: "binary", minBytes: 0, maxBytes: 1048577 };
    },
    "a text minimum above its maximum": (manifest) => {
      const projected = manifest.types.find((entry) => entry.struct?.name === "projected_field");
      projected.struct.fields[0].type.minBytes = 257;
    },
    "aggregate metadata on a non-binary field": (manifest) => {
      const operations = manifest.types.find((entry) => entry.union?.name === "result_operation");
      operations.union.variants.find((variant) => variant.name === "insert").fields[2].type.aggregateBytes.field = "identity";
    },
    "aggregate metadata on a non-structured array": (manifest) => {
      const subscribe = manifest.messages.find((entry) => entry.name === "subscribe");
      subscribe.fields[2].type = {
        kind: "array",
        maxItems: 4,
        items: { kind: "text", maxBytes: 8 },
        aggregateBytes: { field: "value", maxBytes: 4 },
      };
    },
    "a map-permitting scan": (manifest) => {
      manifest.scan.maxMapEntries = 16;
    },
    "extensions permitted by the scan": (manifest) => {
      manifest.scan.extensionsAllowed = true;
    },
    "float32 permitted by the scan": (manifest) => {
      manifest.scan.float32Allowed = true;
    },
    "a repeated enum value": (manifest) => {
      const errorEnum = manifest.types.find((entry) => entry.enum?.name === "error_code");
      errorEnum.enum.values[1].value = errorEnum.enum.values[0].value;
    },
    "a repeated union discriminant": (manifest) => {
      const union = manifest.types.find((entry) => entry.union?.name === "result_operation");
      union.union.variants[1].discriminant = union.union.variants[0].discriminant;
    },
    "a reserved field name": (manifest) => {
      manifest.messages.find((entry) => entry.name === "region_route_change").fields[0].name = "delete";
    },
    "a reserved-word type name": (manifest) => {
      manifest.types[0].struct.name = "class";
    },
    "a message without a tag": (manifest) => {
      delete manifest.messages[0].tag;
    },
    "an unknown direction": (manifest) => {
      manifest.messages[0].direction = "sideways";
    },
    "a tampered envelope shape": (manifest) => {
      manifest.envelope.elements[3].name = "discriminant";
    },
  };

  for (const [name, tamper] of Object.entries(malformedManifests)) {
    test(`the generator rejects a manifest with ${name}`, async () => {
      const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
      tamper(manifest);
      const directory = await mkdtemp(path.join(tmpdir(), "terrascale-msgpack-bad-"));
      const badPath = path.join(directory, "bad-manifest.json");
      await writeFile(badPath, JSON.stringify(manifest, null, 2), "utf8");
      const outDirectory = await mkdtemp(path.join(tmpdir(), "terrascale-msgpack-out-"));
      const result = runGenerator("--manifest", badPath, "--out", outDirectory);
      assert.notEqual(result.status, 0, `the generator accepted ${name}: ${result.stdout}`);
      assert.match(result.stderr, /manifest .*: /u);
      const entries = await readdir(outDirectory);
      assert.deepEqual(entries, [], "a rejected manifest must emit nothing");
    });
  }
});
