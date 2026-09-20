import { strict as assert } from "node:assert";
import { createHash } from "node:crypto";
import { createServer } from "node:http";
import { execFile } from "node:child_process";
import { mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { test as vitestTest } from "vitest";

const execFileAsync = promisify(execFile);

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

vitestTest("contract archive pin is the producer handoff", async () => {
  const pin = (await readFile(path.join(root, "CONTRACT_ARCHIVE_SHA256"), "utf8")).trim();
  assert.match(pin, /^[0-9a-f]{64}$/u);
  assert.equal(pin, "b0b0297f3facf5c0ed9cc17b86a6cec477db4babad47ac9a62ce7a2e2134502b");
});

vitestTest("edge archive URLs fail closed without an immutable digest", async () => {
  const pinPath = path.join(root, "EDGE_CONTRACT_ARCHIVE_SHA256");
  const committedPin = await readFile(pinPath, "utf8").catch(() => undefined);
  if (committedPin !== undefined) assert.match(committedPin.trim(), /^[0-9a-f]{64}$/u);
  const previous = {
    source: process.env.EDGE_CONTRACT_SOURCE,
    url: process.env.EDGE_CONTRACT_URL,
    digest: process.env.EDGE_CONTRACT_ARCHIVE_SHA256,
  };
  delete process.env.EDGE_CONTRACT_SOURCE;
  process.env.EDGE_CONTRACT_URL = "https://contracts.example.invalid/edge-v1.tar";
  delete process.env.EDGE_CONTRACT_ARCHIVE_SHA256;
  try {
    const { resolveEdgeContractBundle } = await import("../scripts/openapi-contract-paths.ts");
    if (committedPin === undefined) {
      await assert.rejects(resolveEdgeContractBundle(), /EDGE_CONTRACT_URL requires a 64-character/);
    } else {
      // The committed pin is the default digest, so the unreachable URL must fail
      // on download rather than bypass integrity checking.
      await assert.rejects(resolveEdgeContractBundle(), /failed to download|fetch failed/);
    }
  } finally {
    if (previous.source === undefined) delete process.env.EDGE_CONTRACT_SOURCE;
    else process.env.EDGE_CONTRACT_SOURCE = previous.source;
    if (previous.url === undefined) delete process.env.EDGE_CONTRACT_URL;
    else process.env.EDGE_CONTRACT_URL = previous.url;
    if (previous.digest === undefined) delete process.env.EDGE_CONTRACT_ARCHIVE_SHA256;
    else process.env.EDGE_CONTRACT_ARCHIVE_SHA256 = previous.digest;
  }
});

vitestTest("the committed edge pin is the default digest for archive URLs", async () => {
  const pinPath = path.join(root, "EDGE_CONTRACT_ARCHIVE_SHA256");
  const committedPin = (await readFile(pinPath, "utf8").catch(() => undefined))?.trim();
  const directory = await mkdtemp(path.join(tmpdir(), "terrascale-edge-pin-"));
  const archive = path.join(directory, "edge.tar");
  await writeFile(archive, "not the published archive\n");
  const archiveBytes = await readFile(archive);
  const server = createServer((_request, response) => { response.writeHead(200, { "content-type": "application/x-tar" }); response.end(archiveBytes); });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const address = server.address();
  assert.ok(address && typeof address === "object");
  const previous = { source: process.env.EDGE_CONTRACT_SOURCE, url: process.env.EDGE_CONTRACT_URL, digest: process.env.EDGE_CONTRACT_ARCHIVE_SHA256 };
  delete process.env.EDGE_CONTRACT_SOURCE;
  process.env.EDGE_CONTRACT_URL = `http://127.0.0.1:${address.port}/edge.tar`;
  delete process.env.EDGE_CONTRACT_ARCHIVE_SHA256;
  try {
    const { resolveEdgeContractBundle } = await import("../scripts/openapi-contract-paths.ts");
    if (committedPin === undefined) {
      await assert.rejects(resolveEdgeContractBundle(), /EDGE_CONTRACT_URL requires a 64-character/);
    } else {
      const actual = createHash("sha256").update(archiveBytes).digest("hex");
      await assert.rejects(resolveEdgeContractBundle(), new RegExp(`archive SHA-256 mismatch \\(${actual} != ${committedPin}\\)`));
    }
  } finally {
    if (previous.source === undefined) delete process.env.EDGE_CONTRACT_SOURCE;
    else process.env.EDGE_CONTRACT_SOURCE = previous.source;
    if (previous.url === undefined) delete process.env.EDGE_CONTRACT_URL;
    else process.env.EDGE_CONTRACT_URL = previous.url;
    if (previous.digest === undefined) delete process.env.EDGE_CONTRACT_ARCHIVE_SHA256;
    else process.env.EDGE_CONTRACT_ARCHIVE_SHA256 = previous.digest;
    await new Promise<void>((resolve) => server.close(() => resolve()));
    await rm(directory, { recursive: true, force: true });
  }
});

vitestTest("edge archive URLs verify the downloaded archive and origin", async () => {
  const directory = await mkdtemp(path.join(tmpdir(), "terrascale-edge-archive-"));
  const bundleDirectory = path.join(directory, "bundle");
  await mkdir(bundleDirectory);
  const artifacts = [
    ["openapi.json", '{"openapi":"3.1.0","paths":{}}\n'],
    ["route-source.json", '{"routes":[]}\n'],
  ] as const;
  const manifestArtifacts = [];
  for (const [name, contents] of artifacts) {
    const bytes = Buffer.from(contents);
    await writeFile(path.join(bundleDirectory, name), bytes);
    manifestArtifacts.push({ path: name, sha256: createHash("sha256").update(bytes).digest("hex") });
  }
  await writeFile(path.join(bundleDirectory, "manifest.json"), JSON.stringify({ schemaVersion: 1, contractVersion: "v1", origin: "edge", artifacts: manifestArtifacts }));
  const archive = path.join(directory, "edge.tar");
  await execFileAsync("tar", ["--sort=name", "--mtime=UTC 1970-01-01", "--owner=0", "--group=0", "--numeric-owner", "-C", bundleDirectory, "-cf", archive, "."]);
  const archiveBytes = await readFile(archive);
  const server = createServer((_request, response) => { response.writeHead(200, { "content-type": "application/x-tar" }); response.end(archiveBytes); });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const address = server.address();
  assert.ok(address && typeof address === "object");
  const previous = { source: process.env.EDGE_CONTRACT_SOURCE, url: process.env.EDGE_CONTRACT_URL, digest: process.env.EDGE_CONTRACT_ARCHIVE_SHA256 };
  delete process.env.EDGE_CONTRACT_SOURCE;
  process.env.EDGE_CONTRACT_URL = `http://127.0.0.1:${address.port}/edge.tar`;
  process.env.EDGE_CONTRACT_ARCHIVE_SHA256 = createHash("sha256").update(archiveBytes).digest("hex");
  try {
    const { resolveEdgeContractBundle } = await import("../scripts/openapi-contract-paths.ts");
    const bundle = await resolveEdgeContractBundle();
    assert.equal(bundle.manifest.origin, "edge");
    assert.equal(bundle.artifactDigests.has("openapi.json"), true);
    await bundle.cleanup();
  } finally {
    if (previous.source === undefined) delete process.env.EDGE_CONTRACT_SOURCE;
    else process.env.EDGE_CONTRACT_SOURCE = previous.source;
    if (previous.url === undefined) delete process.env.EDGE_CONTRACT_URL;
    else process.env.EDGE_CONTRACT_URL = previous.url;
    if (previous.digest === undefined) delete process.env.EDGE_CONTRACT_ARCHIVE_SHA256;
    else process.env.EDGE_CONTRACT_ARCHIVE_SHA256 = previous.digest;
    await new Promise<void>((resolve) => server.close(() => resolve()));
    await rm(directory, { recursive: true, force: true });
  }
});
