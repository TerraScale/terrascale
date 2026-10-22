import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "vitest";

const ownedCommands = [
  "node scripts/provision-reviewed-npm.js",
  "npm ci",
  "npm run typecheck",
  "npm run build",
  "npm run check:v1-route-manifest",
  "npm test",
];

test("pull requests to main and SDK stack bases plus merges to main run CI on the reviewed Node 22 and 24 runtimes", async () => {
  const workflow = await readFile(new URL("../.github/workflows/ci.yml", import.meta.url), "utf8");
  assert.match(workflow, /^on:\n  push:\n    branches: \[main\]\n  pull_request:\n    branches: \[main, "fix/sdk-parity-20261022\*"\]\n/mu);
  assert.doesNotMatch(workflow, /^  (?:pull_request_target|workflow_dispatch|schedule):/mu);
  assert.match(workflow, /^permissions:\n  contents: read\n/mu);
  const job = jobDefinition(workflow, "verify");
  assert.match(job, /runs-on: ubuntu-24\.04/u);
  assert.match(job, /node: \["22\.23\.2", "24\.21\.0"\]/u);
  assert.match(job, /actions\/checkout@11d5960a326750d5838078e36cf38b85af677262/u);
  assert.match(job, /persist-credentials: false/u);
  assert.match(job, /actions\/setup-node@49933ea5288caeca8642d1e84afbd3f7d6820020/u);
  assert.match(job, /node-version: \$\{\{ matrix\.node \}\}/u);
});

test("CI has no switches: no draft gate, fail-fast, conditions, or variables", async () => {
  const workflow = await readFile(new URL("../.github/workflows/ci.yml", import.meta.url), "utf8");
  assert.doesNotMatch(workflow, /^\s+if:|fail-fast|continue-on-error|draft|inputs\.|vars\.|enabled|flag|toggle/imu);
});

test("CI runs exactly the reviewed steps in order", async () => {
  const workflow = await readFile(new URL("../.github/workflows/ci.yml", import.meta.url), "utf8");
  const parsed = steps(jobDefinition(workflow, "verify"));
  assert.deepEqual(parsed.map((step) => step.name), [
    "Check out the pull request revision",
    "Require the owner-approved 0.0.x version policy",
    "Set up the reviewed Node runtime",
    "Provision the reviewed npm CLI",
    "Install locked dependencies",
    "Typecheck",
    "Generate SDK type declarations",
    "Check C# route drift",
    "Run SDK tests",
  ]);
  const commands = parsed.flatMap((step) => {
    const run = runOf(step);
    return run === undefined || run.startsWith("set -euo") ? [] : [run];
  });
  assert.deepEqual(commands, ownedCommands);
});

test("CI never invokes or provisions TerraBase, downloads a producer, or lowers CPU priority", async () => {
  const workflow = await readFile(new URL("../.github/workflows/ci.yml", import.meta.url), "utf8");
  assert.doesNotMatch(workflow, /terrabase|integration|provision-terrabase|run-terrabase|scripts\/ci\/|contracts\/|test:browser|e2e/iu);
  assert.doesNotMatch(workflow, /\b(?:curl|wget|gh release|nice|renice)\b/u);
  assert.doesNotMatch(workflow, /secrets\.|NPM_TOKEN|REGISTRY_TOKEN|TERRASCALE_|FORGEJO/u);
});

test("the deploy profiles run no TerraBase producer command", async () => {
  const deploy = JSON.parse(await readFile(new URL("../.terrascale/deploy.json", import.meta.url), "utf8"));
  const commands = [
    ...deploy.prProfiles,
    ...deploy.releaseProfiles,
    ...deploy.buildTargets,
  ].flatMap((profile) => profile.commands);
  assert.ok(commands.length > 0);
  for (const command of commands) {
    assert.ok(["npm", "node"].includes(command.command), command.description);
    assert.doesNotMatch(
      [command.command, ...command.arguments].join(" "),
      /terrabase|integration|provision|producer|scripts\/ci\//iu,
      command.description,
    );
  }
  assert.doesNotMatch(JSON.stringify(deploy), /test:integration|provision:terrabase/u);
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
