import { execFileSync, spawnSync } from "node:child_process";
import { cp, mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { expect, test } from "vitest";
import { digest, historicalOperations, producerContract, releasePin } from "../scripts/terrabase-contract.mjs";
import { v1Routes } from "../src/v1/routes.js";

test("contract inputs come from one SHA-bound C# release on main", async () => {
  const spec = await producerContract();
  expect(releasePin.producer).toBe("TerraScale/terrabase");
  expect(releasePin.branch).toBe("main");
  expect(releasePin.release).toContain(releasePin.revision);
  expect(releasePin.sha256).toMatch(/^[a-f0-9]{64}$/u);
  expect(spec.components.schemas).toMatchObject({
    WriteBody: { required: ["id", "version", "offset", "durability"] },
    DocumentPageBody: { properties: { next_cursor: { type: ["null", "string"] } } },
    ShapeBody: { required: ["shape_id", "handle", "offset", "next_offset", "up_to_date", "changes"] },
  });
});

test("released data routes use tb_ keys while the published OpenAPI declares the JWT scheme", async () => {
  const spec = await producerContract();
  expect(spec).toMatchObject({ info: { version: releasePin.version } });
  expect(spec.components).toMatchObject({ securitySchemes: { TsAuth: { type: "http", scheme: "bearer", bearerFormat: "JWT" } } });
  for (const route of v1Routes.filter(route => historicalOperations.has(route.operation)))
    expect(spec.paths[route.pathTemplate]?.[route.method.toLowerCase()]).toHaveProperty("security", [{ TsAuth: [] }]);
  expect(spec.paths["/v1/management/organizations"]).toBeUndefined(); // Management-user routes are source-bound, not public.
  expect(spec.paths["/v1/projects/{project}/databases"]?.post).toBeDefined();
  expect(spec.paths["/admin/api-keys"]).toBeUndefined(); // The public OpenAPI excludes admin-only routes.
  /** @param {string} name */
  const source = name => readFile(new URL(`../contracts/terrabase-csharp/${name}`, import.meta.url), "utf8");
  expect(await source("EngineApiKeyStore.cs")).toContain('SecretPrefix = "tb_"');
  expect(await source("ApiKeyAuthenticationHandler.cs")).toContain("secret.StartsWith(EngineApiKeyStore.SecretPrefix");
  expect(await source("api.md")).toContain("Hidden compatibility paths still accept existing `tb_`");
});

test("vendored contracts and security evidence stay bound to the repository-backed release pin", async () => {
  const fixture = await mkdtemp(path.join(tmpdir(), "terrabase-contract-"));
  try {
    for (const file of ["scripts/terrabase-contract.mjs", "scripts/terrabase-current-source.mjs", "scripts/ci/contract-digest.mjs", "src/v1/routes.js", "src/v1/management-routes.js", "src/v1/file-storage-routes.js"]) {
      const target = path.join(fixture, file);
      await mkdir(path.dirname(target), { recursive: true });
      await cp(new URL(`../${file}`, import.meta.url), target);
    }
    await cp(new URL("../contracts/terrabase-current-source", import.meta.url), path.join(fixture, "contracts/terrabase-current-source"), { recursive: true });
    await cp(new URL("../contracts/terrabase-csharp", import.meta.url), path.join(fixture, "contracts/terrabase-csharp"), { recursive: true });
    await writeFile(path.join(fixture, "package.json"), JSON.stringify({ type: "module" }));
    const repo = path.join(fixture, "producer");
    await mkdir(repo);
    for (const [name, input] of Object.entries(releasePin.inputs)) {
      const bytes = await readFile(new URL(`../contracts/terrabase-csharp/${name}`, import.meta.url));
      expect(digest(bytes)).toBe(input.sha256);
      const target = path.join(repo, input.sourcePath);
      await mkdir(path.dirname(target), { recursive: true });
      await writeFile(target, bytes);
    }
    execFileSync("git", ["init", "--quiet", repo]);
    execFileSync("git", ["-C", repo, "add", "."]);
    execFileSync("git", ["-C", repo, "-c", "user.name=Contract Test", "-c", "user.email=contract-test@example.invalid", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=/dev/null", "commit", "--quiet", "-m", "Exact pinned producer source bytes"]);
    const revision = execFileSync("git", ["-C", repo, "rev-parse", "HEAD"], { encoding: "utf8" }).trim();
    // Only the isolated fixture points at this temporary commit; all source and binary hashes stay pinned.
    await writeFile(path.join(fixture, "scripts/ci/terrabase-release.json"), JSON.stringify({ ...releasePin, revision }));
    const run = () => spawnSync(process.execPath, ["--input-type=module", "-e", "import { producerContract } from './scripts/terrabase-contract.mjs'; await producerContract();"], {
      cwd: fixture, encoding: "utf8",
      env: { ...process.env, TERRABASE_REPO: repo, TB_V1_REQUIRED: "0", TERRABASE_V1_OPENAPI: undefined },
    });
    const baseline = run();
    expect(baseline.status, baseline.stderr).toBe(0);
    for (const name of ["IApiKeyStore.cs", "GetDocumentEndpoint.cs"]) {
      const target = path.join(fixture, "contracts/terrabase-csharp", name);
      const original = await readFile(target);
      await writeFile(target, Buffer.concat([original, Buffer.from("\n// Altered vendored security evidence.\n")]));
      const changed = run();
      expect(changed.status).not.toBe(0);
      expect(changed.stderr).toContain(`${name} differs from the pinned C# producer source.`);
      await writeFile(target, original);
    }
    await writeFile(path.join(fixture, "scripts/ci/terrabase-release.json"), JSON.stringify({
      ...releasePin, revision, openapiSha256: "0".repeat(64),
    }));
    const inconsistentPin = run();
    expect(inconsistentPin.status).not.toBe(0);
    expect(inconsistentPin.stderr).toContain("Normalized vendored C# OpenAPI differs from the pinned runtime contract.");
  } finally {
    await rm(fixture, { recursive: true, force: true });
  }
});
