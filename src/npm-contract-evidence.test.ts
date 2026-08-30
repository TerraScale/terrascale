import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const loader = import(
  pathToFileURL(path.join(repositoryRoot, "scripts", "npm-contract-evidence.mjs")).href
) as Promise<{
  loadNpmContractEvidence(
    root: string,
    packedRouteManifestPath?: string,
  ): Promise<ContractEvidence>;
}>;

test("npm contract evidence binds the reviewed descriptor and packed route bytes", async () => {
  const evidence = await (await loader).loadNpmContractEvidence(
    repositoryRoot,
    path.join(repositoryRoot, "sdk-route-manifest.json"),
  );

  assert.deepEqual(evidence, {
    descriptorVersion: 2,
    descriptorSha256: await fileSha256(
      path.join(repositoryRoot, "sdk-openapi-contract-descriptor.json"),
    ),
    openApiSha256: "f9b2207696282e832eeae08e2e7168924442fdb65098e9521e97ff910fa91131",
    routeManifestSha256: await fileSha256(path.join(repositoryRoot, "sdk-route-manifest.json")),
    routeCount: 91,
  });
});

test("npm contract evidence refuses stale, malformed, or substituted inputs", async () => {
  const directory = await mkdtemp(path.join(tmpdir(), "terrascale-npm-contract-evidence-"));
  const manifestPath = path.join(directory, "sdk-route-manifest.json");
  const descriptorPath = path.join(directory, "sdk-openapi-contract-descriptor.json");
  const packedPath = path.join(directory, "packed-route-manifest.json");
  const manifest = `${JSON.stringify({ routeCount: 1, routes: [{}] })}\n`;
  const manifestSha256 = sha256(Buffer.from(manifest));
  const validDescriptor = {
    descriptorVersion: 2,
    routeCount: 1,
    routes: [{}],
    sources: {
      openApiSha256: "a".repeat(64),
      routeManifestSha256: manifestSha256,
    },
  };

  try {
    await writeFile(manifestPath, manifest);
    await writeFile(packedPath, manifest);
    await writeFile(descriptorPath, JSON.stringify(validDescriptor));
    await (await loader).loadNpmContractEvidence(directory, packedPath);

    for (const [mutation, message] of [
      [{ descriptorVersion: 1 }, /version 2/u],
      [{ routeCount: 2, routes: [{}, {}] }, /routeCount values disagree/u],
      [{ routes: [] }, /descriptor routes must match/u],
      [{ sources: { ...validDescriptor.sources, openApiSha256: "A".repeat(64) } }, /lowercase/u],
      [{ sources: { ...validDescriptor.sources, routeManifestSha256: "b".repeat(64) } }, /does not bind/u],
    ] as const) {
      await writeFile(descriptorPath, JSON.stringify({ ...validDescriptor, ...mutation }));
      await assert.rejects(
        (await loader).loadNpmContractEvidence(directory, packedPath),
        message,
      );
    }

    await writeFile(descriptorPath, JSON.stringify(validDescriptor));
    await writeFile(packedPath, `${manifest} `);
    await assert.rejects(
      (await loader).loadNpmContractEvidence(directory, packedPath),
      /Packed SDK route manifest differs/u,
    );

    await writeFile(descriptorPath, "not json");
    await assert.rejects(
      (await loader).loadNpmContractEvidence(directory, manifestPath),
      /must contain valid JSON/u,
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

type ContractEvidence = {
  readonly descriptorVersion: number;
  readonly descriptorSha256: string;
  readonly openApiSha256: string;
  readonly routeManifestSha256: string;
  readonly routeCount: number;
};

async function fileSha256(filePath: string): Promise<string> {
  return sha256(await readFile(filePath));
}

function sha256(bytes: Uint8Array): string {
  return createHash("sha256").update(bytes).digest("hex");
}
