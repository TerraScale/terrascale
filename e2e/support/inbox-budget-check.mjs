import { execFileSync } from 'node:child_process';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { assertRunningBudget, journeySource, legacyCaps, productionInboxRunMs, productionSuiteMs } from './inbox-budget.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const hostnames = readFileSync(join(here, '../env/hostnames'), 'utf8');
const budget = assertRunningBudget(process.env, hostnames);
const line = `production inbox budget phase=${budget.phase} samplePoll=${budget.samplePollDone} pollDone=${budget.pollDone} runMs=${budget.runMs} suiteMs=${budget.suiteMs} stepMinutes=${budget.stepMinutes} jobMinutes=${budget.jobMinutes} hosts=${budget.hosts}`;
console.log(line);

const released = process.env.RELEASED_ROOT;
if (!released) process.exit(0);

const modulePath = join(released, 'e2e/support/inbox-budget.mjs');
let inner;
if (existsSync(modulePath)) {
  const releasedModule = await import(pathToFileURL(modulePath).href);
  inner = { runMs: releasedModule.productionInboxRunMs(), suiteMs: releasedModule.productionSuiteMs() };
} else {
  inner = legacyCaps(readFileSync(join(released, 'e2e/support/inbox-provider.ts'), 'utf8'), readFileSync(join(released, 'e2e/playwright.config.ts'), 'utf8'));
  if (!inner) throw new Error(`released harness at ${released} has no readable provider or suite cap`);
}
const releasedSha = process.env.RELEASED_SHA;
const workflowSha = process.env.WORKFLOW_SHA;
if (!releasedSha || !workflowSha) throw new Error('released harness check needs RELEASED_SHA and WORKFLOW_SHA');
if (releasedSha === workflowSha && (inner.runMs < budget.runMs || inner.suiteMs < budget.suiteMs)) {
  throw new Error(`released harness cannot honor the schedule: provider ${inner.runMs}ms suite ${inner.suiteMs}ms, need run ${budget.runMs}ms suite ${budget.suiteMs}ms. The workflow SHA is that same source, so the new budget does not apply.`);
}
const files = releasedSha === workflowSha ? [] : await changedFiles(releasedSha, workflowSha);
const decision = journeySource(inner, { runMs: productionInboxRunMs(), suiteMs: productionSuiteMs() }, files);
if (decision.source === 'blocked') {
  const shown = decision.inputs.slice(0, 20).join(', ');
  throw new Error(`released harness cannot honor the schedule: provider ${inner.runMs}ms suite ${inner.suiteMs}ms, need run ${productionInboxRunMs()}ms suite ${productionSuiteMs()}ms. App inputs differ: ${shown}. The new budget does not apply to this harness.`);
}
console.log(`production journey source=${decision.source} providerMs=${inner.runMs} suiteMs=${inner.suiteMs}`);

async function changedFiles(base, head) {
  try {
    return execFileSync('git', ['diff', '--name-only', base, head], { encoding: 'utf8' }).trim().split('\n').filter(Boolean);
  } catch {
    const token = process.env.GH_TOKEN || process.env.GITHUB_TOKEN;
    const repo = process.env.GITHUB_REPOSITORY;
    if (!token || !repo) throw new Error(`cannot compare ${base} and ${head}; the new budget does not apply to an unidentified harness`);
    const response = await fetch(`https://api.github.com/repos/${repo}/compare/${base}...${head}`, {
      headers: { authorization: `Bearer ${token}`, accept: 'application/vnd.github+json' },
    });
    if (!response.ok) throw new Error(`cannot compare app inputs: GitHub ${response.status}`);
    const body = await response.json();
    const names = (body.files ?? []).map((file) => file.filename);
    if (names.length >= 300) throw new Error('app-input comparison is incomplete; the new budget does not apply to this harness');
    return names;
  }
}
