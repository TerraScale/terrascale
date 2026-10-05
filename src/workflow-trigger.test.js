import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "vitest";

test("CI preserves event identity, real C# producer and framework/browser checks", async () => {
  const workflow = await readFile(
    new URL("../.forgejo/workflows/ci.yml", import.meta.url),
    "utf8",
  );
  assert.match(workflow, /push:\n    branches: \[develop, main\]/u);
  assert.match(workflow, /pull_request:\n    branches: \[main\]/u);
  assert.match(workflow, /permissions:\n  contents: read/u);
  assert.doesNotMatch(
    workflow,
    /pull_request_target|CONTRACT_ARCHIVE|EDGE_CONTRACT_SOURCE|contracts\/v1|TERRASCALE_DEPLOY/u,
  );
  for (const job of ["static", "node", "browser"]) {
    const definition = jobDefinition(workflow, job);
    assert.doesNotMatch(definition, /NPM_TOKEN/u);
    assert.match(definition, /runs-on: terrascale-builder/u);
    assert.match(definition, /actions\/checkout@[0-9a-f]{40}/u);
    assert.match(definition, /ref: \$\{\{ forgejo\.sha \}\}/u);
    assert.match(definition, /persist-credentials: false/u);
    assert.match(definition, /node scripts\/provision-reviewed-npm\.js/u);
    assert.match(definition, /npm ci/u);
    assert.match(definition, /npm run build/u);
  }
  const node = jobDefinition(workflow, "node");
  assert.match(node, /node scripts\/ci\/provision-terrabase\.mjs/u);
  assert.match(node, /FORGEJO_TOKEN: \$\{\{ secrets\.REGISTRY_TOKEN \}\}/u);
  assert.match(node, /node scripts\/ci\/run-terrabase-integration\.mjs npm test/u);
  assert.ok(node.indexOf("provision-terrabase.mjs") < node.indexOf("run-terrabase-integration.mjs"));
  assert.match(jobDefinition(workflow, "static"), /npm run check:v1-route-manifest/u);
  assert.doesNotMatch(workflow, /provision-current-contract-inputs|canonical effect vectors|check:current-contract|check:route-manifest|check:openapi-contract-descriptor|nice -n|renice/u);
  assert.match(jobDefinition(workflow, "static"), /npm run typecheck/u);
  assert.match(jobDefinition(workflow, "node"), /npm run check:frameworks/u);
  assert.match(jobDefinition(workflow, "node"), /sha256sum --check --strict/u);
  assert.match(jobDefinition(workflow, "node"), /node: "22\.23\.2"/u);
  assert.match(jobDefinition(workflow, "node"), /node: "24\.21\.0"/u);
  assert.match(jobDefinition(workflow, "browser"), /npm run test:browser/u);
  assert.match(
    jobDefinition(workflow, "browser"),
    /bash scripts\/ci\/provision-branded-browsers\.sh/u,
  );
});

test("npm publication runs only after verification on pushes to main", async () => {
  const workflow = await readFile(
    new URL("../.forgejo/workflows/ci.yml", import.meta.url),
    "utf8",
  );
  const publish = jobDefinition(workflow, "publish");
  assert.match(publish, /needs: \[static, node, browser\]/u);
  assert.match(
    publish,
    /if: \$\{\{ forgejo\.event_name == 'push' && forgejo\.ref == 'refs\/heads\/main' \}\}/u,
  );
  assert.match(publish, /NPM_TOKEN: \$\{\{ secrets\.NPM_TOKEN \}\}/u);
  assert.match(publish, /npm publish artifacts\/release\/\*\.tgz --access public --ignore-scripts --tag latest/u);
  assert.equal(workflow.match(/NPM_TOKEN: \$\{\{ secrets\.NPM_TOKEN \}\}/gu)?.length, 1);
  assert.doesNotMatch(workflow, /npm (?:unpublish|deprecate)|terrascale@/u);
  assert.doesNotMatch(stepDefinition(publish, "Provision the reviewed npm CLI"), /^        if:/mu);
  assert.match(publish, /npm view "\$\{name\}" dist-tags\.latest --prefer-online --registry https:\/\/registry\.npmjs\.org\//u);
  assert.match(publish, /TS-528: expected npm latest \$\{version\}, got \$\{latest\}/u);
});

test("the real static workflow guard accepts only owner-approved 0.0.x versions", async () => {
  const workflow = await readFile(new URL("../.forgejo/workflows/ci.yml", import.meta.url), "utf8");
  const guard = stepRun(stepDefinition(jobDefinition(workflow, "static"), "Require the owner-approved 0.0.x version policy"));
  const directory = await mkdtemp(path.join(tmpdir(), "terrascale-version-guard-"));
  try {
    for (const version of ["0.0.0", "0.0.1", "0.0.528", "0.3.0", "1.0.0", "0.0", "0.0.1-beta", "0.0.1+build"]) {
      await writeFile(path.join(directory, "package.json"), JSON.stringify({ version }));
      const result = spawnSync("bash", ["-e", "-o", "pipefail", "-c", guard], {
        cwd: directory, encoding: "utf8", timeout: 10_000,
      });
      assert.ifError(result.error);
      const valid = ["0.0.0", "0.0.1", "0.0.528"].includes(version);
      assert.equal(result.status, valid ? 0 : 1, version);
      assert.equal(result.stderr, valid ? "" : `TS-528: 0.0.x is required until the owner lifts it; got ${version}\n`);
    }
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test.each(["published", "delayed", "wrong-latest"])("the real npm publish shell verifies latest when %s", async (scenario) => {
  const workflow = await readFile(new URL("../.forgejo/workflows/ci.yml", import.meta.url), "utf8");
  const publish = stepRun(stepDefinition(jobDefinition(workflow, "publish"), "Pack and publish the release tarball"));
  const directory = await mkdtemp(path.join(tmpdir(), "terrascale-npm-publish-"));
  try {
    await writeFile(path.join(directory, "package.json"), JSON.stringify({ name: "@terrascale/sdk", version: "0.0.1" }));
    // This npm CLI double never forwards commands to a real registry.
    await writeFile(path.join(directory, "npm"), `#!/usr/bin/env bash
set -euo pipefail
printf '%s\\n' "$*" >> calls
case "$1" in
  whoami) echo terrascale ;;
  pack) touch artifacts/release/terrascale-sdk-0.0.1.tgz ;;
  publish) touch published ;;
  view)
    test -f published
    if [ "$NPM_CASE" = delayed ] && [ ! -f seen ]; then touch seen; echo 'npm error code E404' >&2; exit 1; fi
    if [ "$NPM_CASE" = wrong-latest ]; then echo 1.0.0; else echo 0.0.1; fi ;;
  *) echo "Unexpected npm command: $*" >&2; exit 98 ;;
esac
`, { mode: 0o755 });
    await writeFile(path.join(directory, "calls"), "");
    const result = spawnSync("bash", ["-e", "-o", "pipefail", "-c", `npm() { bash ./npm "$@"; }\nsleep() { :; }\n${publish}`], {
      cwd: directory, encoding: "utf8", timeout: 10_000,
      env: { PATH: process.env.PATH ?? "", RUNNER_TEMP: directory, NPM_TOKEN: "workflow-test-token", NPM_CASE: scenario },
    });
    assert.ifError(result.error);
    assert.equal(result.status, scenario === "wrong-latest" ? 1 : 0, result.stderr);
    assert.match(result.stderr, scenario === "wrong-latest" ? /TS-528: expected npm latest 0\.0\.1, got 1\.0\.0\n$/u : scenario === "delayed" ? /^npm error code E404\n$/u : /^$/u);
    const view = "view @terrascale/sdk dist-tags.latest --prefer-online --registry https://registry.npmjs.org/";
    const views = scenario === "published" ? 1 : scenario === "delayed" ? 2 : 6;
    assert.equal(await readFile(path.join(directory, "calls"), "utf8"), [
      "whoami --registry https://registry.npmjs.org/",
      "pack --ignore-scripts --pack-destination artifacts/release",
      "publish artifacts/release/terrascale-sdk-0.0.1.tgz --access public --ignore-scripts --tag latest --registry https://registry.npmjs.org/",
      ...Array(views).fill(view),
    ].map((call) => `${call}\n`).join(""));
    await assert.rejects(readFile(path.join(directory, "npmrc")), { code: "ENOENT" });
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

/** @param {string} definition @param {string} name */
function stepDefinition(definition, name) {
  const step = definition.split(`      - name: ${name}\n`)[1]?.split(/\n      - name:/u)[0];
  assert.ok(step, `Missing workflow step: ${name}`);
  return step;
}

/** @param {string} step */
function stepRun(step) {
  const run = step.match(/        run: \|\n((?:          [^\n]*(?:\n|$))+)/u)?.[1];
  assert.ok(run, "Missing workflow shell block");
  return run.replace(/^          /gmu, "");
}

/**
 * @param {string} workflow
 * @param {string} job
 * @returns {string}
 */
function jobDefinition(workflow, job) {
  const start = workflow.indexOf(`  ${job}:\n`);
  assert.notEqual(start, -1);
  const remaining = workflow.slice(start);
  const next = remaining.slice(1).search(/\n  [a-z][a-z0-9-]+:\n/u);
  return next === -1 ? remaining : remaining.slice(0, next + 1);
}
