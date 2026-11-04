import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { access, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "vitest";

const ciUrl = new URL("../../../.github/workflows/sdk-js-ci.yml", import.meta.url);
const proofUrl = new URL("../../../.github/workflows/sdk-fast.yml", import.meta.url);
const qualificationUrl = new URL("../scripts/qualify-release.sh", import.meta.url);
const ownedCommands = [
  "node scripts/provision-reviewed-npm.js",
  "npm run check:vulnerabilities",
  "npm ci",
  "npm run typecheck",
  "npm run build",
  "npm run check:v1-route-manifest",
  "npm test",
];

test("SDK changes require the shared Fast proof with read-only workflow permissions", async () => {
  const workflow = await readFile(ciUrl, "utf8");
  assert.match(workflow, /^  push:\n    branches: \[main\]/mu);
  assert.match(workflow, /^  pull_request:\n    branches: \[main\]/mu);
  assert.match(workflow, /'sdk\/js\/\*\*'/u);
  assert.doesNotMatch(workflow, /^  pull_request_target:/mu);
  assert.match(workflow, /^permissions:\n  contents: read\n  statuses: read\n/mu);
  assert.match(workflow, /uses: \.\/\.github\/workflows\/sdk-fast\.yml/u);
  assert.match(workflow, /area: sdk-js/u);
  assert.doesNotMatch(workflow, /runs-on:|npm (?:ci|test|run)/u);
});

test("shared CI checks the exact source commit's dev-host proof without building", async () => {
  const workflow = await readFile(proofUrl, "utf8");
  assert.match(workflow, /persist-credentials: false/u);
  assert.match(workflow, /github\.event\.pull_request\.head\.sha \|\| github\.sha/u);
  assert.match(workflow, /python3 scripts\/sdk\/check-proof\.py "\$AREA" "\$REVISION"/u);
  assert.doesNotMatch(workflow, /npm (?:ci|test|run)|dotnet (?:build|test)|cargo (?:build|test)|go test/u);
  assert.doesNotMatch(workflow, /secrets\.|NPM_TOKEN|REGISTRY_TOKEN/u);
});

test("dev-host qualification retains audits, static checks, tests and packing", async () => {
  const qualification = await readFile(qualificationUrl, "utf8");
  let previous = -1;
  for (const command of ownedCommands) {
    const position = qualification.indexOf(command);
    assert.ok(position > previous, `Missing or reordered release gate: ${command}`);
    previous = position;
  }
  assert.match(qualification, /inspectNpmCli/u);
  assert.match(qualification, /TERRASCALE_NPM_ARCHIVE_INTEGRITY/u);
  assert.match(qualification, /npm pack --json --pack-destination "\$output"/u);
  assert.doesNotMatch(qualification, /--allow-dirty|npm publish/u);
});

test("SDK CI and qualification never invoke or download a TerraBase binary", async () => {
  for (const url of [ciUrl, proofUrl, qualificationUrl]) {
    const contents = await readFile(url, "utf8");
    assert.doesNotMatch(contents, /provision-terrabase|run-terrabase|terrabase[^\n]*(?:download|binary)|gh release download[^\n]*terrabase/iu);
    assert.doesNotMatch(contents, /\b(?:nice|renice)\b/u);
  }
});

test("npm exposes no TerraBase binary provisioning or integration command", async () => {
  const pkg = JSON.parse(await readFile(new URL("../package.json", import.meta.url), "utf8"));
  assert.equal(pkg.scripts["provision:terrabase"], undefined);
  assert.equal(pkg.scripts["test:integration"], undefined);
  assert.doesNotMatch(JSON.stringify(pkg.scripts), /provision-terrabase|run-terrabase-integration/u);
});

test("TerraBase binary helpers and their isolated provisioning support are absent", async () => {
  for (const file of [
    "scripts/ci/provision-terrabase.mjs",
    "scripts/ci/run-terrabase-integration.mjs",
    "scripts/ci/terrabase-lifecycle.mjs",
    "scripts/ci/provision-terrabase-tenant.mjs",
    "test/support/terrabase-v1-setup.js",
    "scripts/ci/terrabase-ci.test.js",
    "scripts/ci/terrabase-provisioning.test.js",
  ]) {
    await assert.rejects(access(new URL(`../${file}`, import.meta.url)), { code: "ENOENT" }, file);
  }
});

test("the owner-approved 0.0.x guard runs before qualification and rejects other versions", async () => {
  const qualification = await readFile(qualificationUrl, "utf8");
  const start = qualification.indexOf("version=$(node -p");
  const end = qualification.indexOf('test "$(node --version)"');
  assert.ok(start >= 0 && end > start, "Missing version guard before toolchain qualification");
  const guard = qualification.slice(start, end);
  const pkg = JSON.parse(await readFile(new URL("../package.json", import.meta.url), "utf8"));
  assert.match(pkg.version, /^0\.0\.[0-9]+$/u);
  const directory = await mkdtemp(path.join(tmpdir(), "terrascale-version-guard-"));
  try {
    for (const version of ["0.0.0", "0.0.1", "0.0.528", "0.3.0", "1.0.0", "0.0", "0.0.1-beta", "0.0.1+build", pkg.version]) {
      await writeFile(path.join(directory, "package.json"), JSON.stringify({ version }));
      const result = spawnSync("bash", ["-e", "-o", "pipefail", "-c", guard], {
        cwd: directory, encoding: "utf8", timeout: 10_000,
      });
      assert.ifError(result.error);
      const valid = /^0\.0\.[0-9]+$/u.test(version);
      assert.equal(result.status, valid ? 0 : 1, version);
      assert.equal(result.stderr, valid ? "" : `TS-528: 0.0.x is required until the owner lifts it; got ${version}\n`);
    }
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
