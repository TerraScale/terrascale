import assert from "node:assert/strict";
import { readdir, readFile } from "node:fs/promises";
import { describe, test } from "node:test";

import {
  runSyncV2Conformance,
  type SyncV2ConformanceReport,
  type SyncV2Corpus,
} from "./index.js";

const fixtureDirectory = new URL("../test/fixtures/terrascale.sync.v2/", import.meta.url);
const encoder = new TextEncoder();

function corpusFrom(directory: URL): SyncV2Corpus {
  return {
    async read(relativePath) {
      const bytes = new Uint8Array(await readFile(new URL(relativePath, directory)));
      return { bytes, text: new TextDecoder("utf-8", { fatal: true }).decode(bytes) };
    },
    async list(relativeDirectory) {
      return readdir(new URL(relativeDirectory, directory), { withFileTypes: true })
        .then((entries) => entries.filter((entry) => entry.isFile()).map((entry) => entry.name).sort());
    },
  };
}

describe("terrascale.sync.v2 conformance (SE-04/SE-05 golden vectors)", () => {
  test("the vendored corpus passes every client-contract check with the manifest gate verified", async () => {
    const report = await runSyncV2Conformance({ corpus: corpusFrom(fixtureDirectory) });

    assert.equal(report.schema, "terrascale.sync.v2/conformance-report@1");
    assert.equal(report.protocol, "terrascale.sync.v2");
    assert.equal(report.protocol_version, 2);
    assert.ok(report.harness.startsWith("@terrascale/sdk"));
    assert.equal(report.corpus, "terrascale.sync.v2");
    assert.equal(report.manifest?.status, "verified");
    assert.deepEqual(report.summary.problems, []);
    assert.equal(report.summary.status, "pass");
    assert.equal(report.summary.vectors_total, 9);
    assert.equal(report.summary.vectors_passed, 9);
    assert.equal(report.summary.vectors_failed, 0);
    assert.equal(report.summary.checks_failed, 0);
    assert.ok(report.summary.checks_total > 0, "the corpus must execute checks");

    const byCategory = new Map(report.vectors.map((vector) => [vector.category, vector]));
    const categories = [
      "fresh-session",
      "checkpoint-resume",
      "reconnect-replay",
      "slow-consumer",
      "region-route-change",
      "resync-required",
      "malformed-frame-rejection",
      "repeated-change-delta",
      "trigger-notification",
    ];
    assert.deepEqual([...byCategory.keys()].sort(), [...categories].sort());
    for (const category of categories) {
      const vector = byCategory.get(category);
      assert.equal(vector?.status, "pass", `${category} must pass`);
      assert.equal(vector?.aborted, false);
      assert.deepEqual(vector?.failures, []);
    }

    // Fail-closed decode: every recorded rejection reproduces its exact
    // typed error through this codec.
    const malformed = byCategory.get("malformed-frame-rejection");
    assert.equal(malformed?.checks["decode_rejects_typed_error"], 10);

    // Trigger notifications stay content-free and inside their budget.
    const trigger = byCategory.get("trigger-notification");
    assert.ok((trigger?.checks["trigger_content_free"] ?? 0) >= 1);
    assert.ok((trigger?.checks["trigger_within_budget"] ?? 0) >= 1);

    // Budget recounting runs from the committed bytes, including the
    // delta-below-snapshot invariant and per-change accounting.
    const repeated = byCategory.get("repeated-change-delta");
    assert.ok((repeated?.checks["budget_within_budget"] ?? 0) >= 1);
    assert.equal(repeated?.checks["budget_delta_below_snapshot"], 1);
    assert.equal(repeated?.checks["budget_bytes_per_delivered_change"], 1);
    const slow = byCategory.get("slow-consumer");
    assert.ok((slow?.checks["budget_frame_decodes"] ?? 0) >= 2);
  });

  test("byte-exactness is proven against the shipped codec, not the recorded numbers", async () => {
    const report = await runSyncV2Conformance({ corpus: corpusFrom(fixtureDirectory) });
    for (const vector of report.vectors) {
      assert.ok(
        (vector.checks["frame_encode_bytes_exact"] ?? 0) > 0
          || (vector.checks["decode_rejects_typed_error"] ?? 0) > 0
          || (vector.checks["budget_frame_decodes"] ?? 0) > 0
          || (vector.checks["serve_frame_bytes_exact"] ?? 0) > 0,
        `${vector.category} must exercise the codec`,
      );
    }
    const fresh = report.vectors.find((vector) => vector.category === "fresh-session");
    assert.ok((fresh?.checks["frame_encode_bytes_exact"] ?? 0) >= 2);
    assert.ok((fresh?.checks["frame_digest_verifies"] ?? 0) >= 2);
    const reconnect = report.vectors.find((vector) => vector.category === "reconnect-replay");
    assert.ok((reconnect?.checks["serve_frame_bytes_exact"] ?? 0) >= 1);
  });

  test("reports are deterministic across runs over the same corpus", async () => {
    const first = await runSyncV2Conformance({ corpus: corpusFrom(fixtureDirectory) });
    const second = await runSyncV2Conformance({ corpus: corpusFrom(fixtureDirectory) });
    assert.equal(JSON.stringify(first), JSON.stringify(second));
  });

  test("the manifest gate fails closed when a vendored vector drifts from the committed corpus", async () => {
    const base = corpusFrom(fixtureDirectory);
    const tampered: SyncV2Corpus = {
      ...base,
      async read(relativePath) {
        const loaded = await base.read(relativePath);
        if (relativePath === "vectors/trigger-notification.json") {
          const text = loaded.text.replace('"sequence": 1', '"sequence": 2');
          return { bytes: encoder.encode(text), text };
        }
        return loaded;
      },
    };
    const report: SyncV2ConformanceReport = await runSyncV2Conformance({ corpus: tampered });

    assert.equal(report.manifest?.status, "failed");
    assert.equal(report.summary.status, "fail");
    assert.ok((report.manifest.problems.length ?? 0) > 0);
  });

  test("a corpus file missing its manifest entry fails the run before vectors execute", async () => {
    const base = corpusFrom(fixtureDirectory);
    const listBase = base.list?.bind(base);
    assert.ok(listBase !== undefined, "the fixture corpus enumerates its files");
    const extra: SyncV2Corpus = {
      ...base,
      async list(relativeDirectory) {
        const files = await listBase(relativeDirectory);
        return relativeDirectory === "vectors"
          ? [...files, "ninth-extra.json"].sort()
          : files;
      },
    };
    const report = await runSyncV2Conformance({ corpus: extra });
    assert.equal(report.summary.status, "fail");
    assert.ok(
      report.summary.problems.some((problem) => problem.includes("ninth-extra.json")),
      `problems must name the unexpected file: ${JSON.stringify(report.summary.problems)}`,
    );
  });
});
