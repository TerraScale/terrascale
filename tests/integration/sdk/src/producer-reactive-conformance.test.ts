import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { test } from "vitest";
import { TerraScaleSyncV1Client } from "./sync/v1/client.js";

// Producer bytes exercise the public decoder and result state machine directly.
// This is contract conformance, not evidence of a running reactive service.
test("reactive client applies the pinned producer snapshot, log, and reset bytes", async () => {
  const { resolveContractBundle } = await import(new URL("../scripts/openapi-contract-paths.ts", import.meta.url).href);
  const bundle = await resolveContractBundle();
  try {
    assert.ok(bundle.artifactDigests.has("conformance/reactive.json"));
    const corpus = JSON.parse(await readFile(path.join(bundle.root, "conformance/reactive.json"), "utf8"));
    const openapi = await readFile(path.join(bundle.root, "openapi.json"));
    assert.equal(corpus.openapiSha256, createHash("sha256").update(openapi).digest("hex"));
    assert.equal(corpus.contractVersion, bundle.manifest.contractVersion);
    const request = corpus.attach.request.body;
    const open = () => {
      const client = new TerraScaleSyncV1Client();
      const { subscription } = client.openSubscription({
        subscription: request.subscription,
        query: request.statement,
        parameters: Buffer.from(request.parameters, "base64"),
      });
      return { client, view: subscription.view };
    };
    const snapshotBytes = Buffer.from(corpus.snapshot.hex, "hex");
    const logBytes = Buffer.from(corpus.log.hex, "hex");
    const resetBytes = Buffer.from(corpus.reset.hex, "hex");
    const { client, view } = open();
    const snapshot = client.receive(snapshotBytes).message;
    assert.equal(snapshot.kind, "snapshot");
    assert.deepEqual(view.getItems(), []);
    assert.equal(view.getRevision(), 0n);
    assert.equal(view.getViewGeneration(), 1n);
    assert.equal(view.getAuthorizationGeneration(), 1n);
    assert.equal(view.getCheckpoint()?.toBase64(), corpus.attach.response.body.checkpoint);

    const log = client.receive(logBytes).message;
    assert.equal(log.kind, "delta");
    assert.equal(view.getRevision(), 1n);
    assert.deepEqual(view.getItems().map(item => ({
      identity: item.identity,
      status: new TextDecoder().decode(item.value.status),
    })), [{ identity: "order-1", status: '"paid"' }]);
    assert.equal(log.kind === "delta" && log.digest, "d12faa372354de79608c8212f5fd2f3207bdf89a37b0242b1ec38d57848e19bc");
    const checkpoint = view.getCheckpoint()!.toBytes();
    assert.deepEqual(client.checkpointAcknowledgement(request.subscription).checkpoint, checkpoint);
    client.receive(logBytes);
    assert.equal(view.getItems().length, 1);
    assert.deepEqual(view.getCheckpoint()!.toBytes(), checkpoint);

    // Reset is the alternative recovery response at revision 1, not a second
    // conflicting snapshot after consuming the log at that same revision.
    const recovering = open();
    recovering.client.receive(snapshotBytes);
    const reset = recovering.client.receive(resetBytes).message;
    assert.equal(reset.kind, "reset");
    assert.equal(reset.kind === "reset" && reset.reason, corpus.reset.reason);
    assert.equal(recovering.view.getRevision(), 1n);
    assert.deepEqual(recovering.view.getItems(), []);
    assert.deepEqual(recovering.view.getCheckpoint()!.toBytes(), checkpoint);

    // Truncated producer frames must leave the last authorized state intact.
    assert.throws(() => client.receive(logBytes.subarray(0, logBytes.length - 1)), /protocol_error/);
    assert.equal(view.getRevision(), 1n);
    assert.equal(view.getItems().length, 1);
    assert.deepEqual(view.getCheckpoint()!.toBytes(), checkpoint);
  } finally {
    await bundle.cleanup();
  }
});
