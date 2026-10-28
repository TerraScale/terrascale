import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { access, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "vitest";

const ownedCommands = [
  "node scripts/provision-reviewed-npm.js",
  "npm run check:vulnerabilities",
  "npm ci",
  "npm run typecheck",
  "npm run build",
  "npm run check:v1-route-manifest",
  "npm test",
];

test("PRs keep one verify job and supported Node 22 compatibility stays weekly", async () => {
  const workflow = await readFile(new URL("../.github/workflows/ci.yml", import.meta.url), "utf8");
  assert.match(workflow, /^  pull_request:\n    branches: \[main\]\n/mu);
  assert.doesNotMatch(workflow, /^  pull_request_target:/mu);
  assert.match(workflow, /^  schedule:/mu);
  assert.match(workflow, /^permissions:\n  contents: read\n/mu);
  const job = jobDefinition(workflow, "verify");
  assert.match(job, /runs-on: ubuntu-24\.04/u);
  assert.doesNotMatch(job, /matrix\./u);
  assert.match(job, /github\.event_name == 'schedule' \|\| github\.event_name == 'workflow_dispatch'\) && '22\.23\.2' \|\| '24\.21\.0'/u);
  assert.match(job, /actions\/checkout@11d5960a326750d5838078e36cf38b85af677262/u);
  assert.match(job, /persist-credentials: false/u);
  assert.match(job, /actions\/setup-node@49933ea5288caeca8642d1e84afbd3f7d6820020/u);
});

test("CI retains the reviewed checks and main skips only an identical proven PR tree", async () => {
  const workflow = await readFile(new URL("../.github/workflows/ci.yml", import.meta.url), "utf8");
  const parsed = steps(jobDefinition(workflow, "verify"));
  const commands = parsed.flatMap((step) => {
    const run = runOf(step);
    return run !== undefined && ownedCommands.includes(run) ? [run] : [];
  });
  assert.deepEqual(commands, ownedCommands);
  for (const name of ["Typecheck", "Generate SDK type declarations", "Check C# route drift", "Run SDK tests"])
    assert.match(stepNamed(jobDefinition(workflow, "verify"), name).body, /steps\.proof\.outputs\.proven != 'true'/u);
  // Advisories change without code changes, so the audit runs on every tree, including proven ones.
  assert.doesNotMatch(stepNamed(jobDefinition(workflow, "verify"), "Audit every committed npm graph").body, /\bif:/u);
  const save = stepNamed(jobDefinition(workflow, "verify"), "Save main npm cache");
  assert.match(save.body, /github\.event_name == 'push'/u);
});

test("CI never invokes or provisions TerraBase, downloads a producer, or lowers CPU priority", async () => {
  const workflow = await readFile(new URL("../.github/workflows/ci.yml", import.meta.url), "utf8");
  assert.doesNotMatch(workflow, /terrabase|integration|provision-terrabase|run-terrabase|contracts\/|test:browser|e2e/iu);
  assert.doesNotMatch(workflow, /\b(?:curl|wget|gh release|nice|renice)\b/u);
  assert.doesNotMatch(workflow, /secrets\.|NPM_TOKEN|REGISTRY_TOKEN|TERRASCALE_|FORGEJO/u);
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

test("the owner-approved 0.0.x guard runs in CI and the real package passes it", async () => {
  const workflow = await readFile(new URL("../.github/workflows/ci.yml", import.meta.url), "utf8");
  const guard = runOf(stepNamed(jobDefinition(workflow, "verify"), "Require the owner-approved 0.0.x version policy"));
  assert.ok(guard !== undefined, "Missing version guard run block");
  const pkg = JSON.parse(await readFile(new URL("../package.json", import.meta.url), "utf8"));
  assert.match(pkg.version, /^0\.0\.[0-9]+$/u);
  const directory = await mkdtemp(path.join(tmpdir(), "terrascale-version-guard-"));
  try {
    for (const version of ["0.0.0", "0.0.1", "0.0.528", "0.3.0", "1.0.0", "0.0", "0.0.1-beta", "0.0.1+build", pkg.version]) {
      await writeFile(path.join(directory, "package.json"), JSON.stringify({ version }));
      /** @type {import("node:child_process").SpawnSyncReturns<string>} */
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

/** @param {string} workflow @param {string} job @returns {string} */
function jobDefinition(workflow, job) {
  const start = workflow.indexOf(`  ${job}:\n`);
  assert.notEqual(start, -1, `Missing workflow job: ${job}`);
  const remaining = workflow.slice(start);
  const next = remaining.slice(1).search(/\n  [a-z][a-z0-9-]+:\n/u);
  return next === -1 ? remaining : remaining.slice(0, next + 1);
}

/** @param {string} definition @returns {{ name: string, body: string }[]} */
function steps(definition) {
  return definition.split("\n      - name: ").slice(1).map((chunk) => {
    const [name = "", ...body] = chunk.split("\n");
    return { name, body: body.join("\n") };
  });
}

/** @param {string} definition @param {string} name @returns {{ name: string, body: string }} */
function stepNamed(definition, name) {
  const step = steps(definition).find((candidate) => candidate.name === name);
  assert.ok(step, `Missing workflow step: ${name}`);
  return step;
}

/** @param {{ body: string }} step @returns {string | undefined} */
function runOf(step) {
  const block = step.body.match(/^        run: \|\n((?:          [^\n]*\n?)+)/mu)?.[1];
  if (block !== undefined) return block.replace(/^          /gmu, "").trimEnd();
  return step.body.match(/^        run: (.+)$/mu)?.[1];
}
