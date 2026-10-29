import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtempSync, writeFileSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "vitest";

const script = fileURLToPath(new URL("./proven-pr.sh", import.meta.url));
const main = "a".repeat(40);
const head = "b".repeat(40);
const tree = "c".repeat(40);
const pull = { merged_at: "2026-10-22T00:00:00Z", state: "closed", base: { ref: "main" }, merge_commit_sha: main, head: { sha: head } };
const run = { id: 123, event: "pull_request", head_sha: head, status: "completed", conclusion: "success", head_repository: { full_name: "contributor/fork" } };
const marker = { name: `Checked tree ${tree}`, status: "completed", conclusion: "success" };
const job = { name: "verify", status: "completed", conclusion: "success", steps: [marker] };

/** @param {{pulls?: unknown[], runs?: unknown[], jobs?: unknown[], fail?: string}} [options] */
function prove({ pulls = [pull], runs = [run], jobs = [job], fail = "" } = {}) {
  const dir = mkdtempSync(path.join(tmpdir(), "ts-js-proof-"));
  try {
    writeFileSync(path.join(dir, "fixture.json"), JSON.stringify({ pulls, runs, jobs, fail }));
    writeFileSync(path.join(dir, "git"), `#!/bin/sh\n[ "$*" = 'rev-parse HEAD^{tree}' ] || exit 1\nprintf '%s\\n' '${tree}'\n`, { mode: 0o755 });
    writeFileSync(path.join(dir, "gh"), `#!/usr/bin/env node
const fs = require('node:fs');
const f = JSON.parse(fs.readFileSync(process.env.FIXTURE, 'utf8'));
const endpoint = process.argv[3];
if (process.argv[2] !== 'api' || !endpoint.startsWith('repos/TerraScale/ts-js/')) process.exit(1);
let kind;
if (endpoint === 'repos/TerraScale/ts-js/commits/${main}/pulls?per_page=100') kind = 'pulls';
else if (endpoint === 'repos/TerraScale/ts-js/actions/runs/123/jobs?per_page=100') kind = 'jobs';
else if (endpoint === 'repos/TerraScale/ts-js/actions/workflows/ci.yml/runs?event=pull_request&head_sha=${head}&status=success&per_page=1') kind = 'runs';
else process.exit(1);
if (f.fail === kind) process.exit(1);
console.log(JSON.stringify(kind === 'pulls' ? f.pulls : kind === 'jobs' ? { jobs: f.jobs } : { workflow_runs: f.runs }));
`, { mode: 0o755 });
    const output = path.join(dir, "output");
    execFileSync("bash", [script], { env: { ...process.env, PATH: `${dir}:${process.env.PATH}`, FIXTURE: path.join(dir, "fixture.json"), GITHUB_REPOSITORY: "TerraScale/ts-js", GITHUB_SHA: main, GITHUB_OUTPUT: output } });
    return readFileSync(output, "utf8");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

test("proves the exact merged tree with successful source-head PR CI, including fork heads", () => {
  assert.equal(prove(), "proven=true\n");
});

test("reruns CI when merged PR identity differs", () => {
  for (const options of [
    { pulls: [] },
    ...[{ merge_commit_sha: head }, { base: { ref: "release" } }, { state: "open" }, { merged_at: null }].map(change => ({ pulls: [{ ...pull, ...change }] })),
  ]) assert.equal(prove(options), "proven=false\n");
});

test("requires successful verify execution and the exact tested tree marker", () => {
  for (const jobs of [
    [],
    ...[{ name: "other" }, { status: "in_progress" }, { conclusion: "failure" }, { conclusion: "skipped" }, { steps: [] }].map(change => [{ ...job, ...change }]),
    ...[{ name: `Checked tree ${"d".repeat(40)}` }, { status: "queued" }, { conclusion: "skipped" }, { conclusion: "failure" }].map(change => [{ ...job, steps: [{ ...marker, ...change }] }]),
  ]) assert.equal(prove({ jobs }), "proven=false\n");
});

test("reruns CI without a completed successful PR run for the exact source head", () => {
  for (const runs of [[], ...[{ conclusion: "failure" }, { status: "in_progress" }, { event: "push" }, { head_sha: main }].map(change => [{ ...run, ...change }])]) {
    assert.equal(prove({ runs }), "proven=false\n");
  }
});

test("API errors fail closed at every evidence request", () => {
  for (const fail of ["pulls", "runs", "jobs"]) assert.equal(prove({ fail }), "proven=false\n");
});
