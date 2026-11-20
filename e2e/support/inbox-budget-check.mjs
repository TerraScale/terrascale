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
const identity = `release=${releasedSha} harness=${releasedSha} runMs=${budget.runMs} suiteMs=${budget.suiteMs} pollDone=${budget.pollDone}`;
if (inner.runMs >= budget.runMs && inner.suiteMs >= budget.suiteMs) {
  console.log(`production journey ${identity} source=released`);
  process.exit(0);
}
if (releasedSha === workflowSha) {
  throw new Error(`released harness cannot honor the schedule: provider ${inner.runMs}ms suite ${inner.suiteMs}ms, need run ${budget.runMs}ms suite ${budget.suiteMs}ms. The workflow SHA is that same source, so the new budget does not apply.`);
}
const files = await changedFiles(releasedSha, workflowSha);
const decision = journeySource(inner, { runMs: productionInboxRunMs(), suiteMs: productionSuiteMs() }, files);
if (decision.source !== 'workflow') {
  const shown = decision.inputs.slice(0, 20).join(', ');
  throw new Error(`released harness cannot honor the schedule: provider ${inner.runMs}ms suite ${inner.suiteMs}ms, need run ${budget.runMs}ms suite ${budget.suiteMs}ms. App inputs differ: ${shown}. The new budget does not apply to this harness.`);
}
console.log(`production journey release=${releasedSha} harness=${workflowSha} runMs=${budget.runMs} suiteMs=${budget.suiteMs} pollDone=${budget.pollDone} source=workflow`);

function diffNames(base, head) {
  const text = execFileSync('git', ['diff', '--no-renames', '--name-status', base, head], { encoding: 'utf8' });
  return text.trim().split('\n').filter(Boolean).flatMap((row) => row.split('\t').slice(1));
}

async function changedFiles(base, head) {
  try {
    return diffNames(base, head);
  } catch {
    const token = process.env.GH_TOKEN || process.env.GITHUB_TOKEN;
    if (token) {
      try {
        execFileSync('git', ['-c', `http.extraheader=AUTHORIZATION: bearer ${token}`, 'fetch', '--depth=1', 'origin', head], { stdio: 'pipe' });
        return diffNames(base, head);
      } catch { /* the compare API is the remaining evidence */ }
    }
    const repo = process.env.GITHUB_REPOSITORY;
    if (!token || !repo) throw new Error(`fallback comparison of ${base} and ${head} is unavailable; the new budget does not apply to this harness`);
    const response = await fetch(`https://api.github.com/repos/${repo}/compare/${base}...${head}?per_page=100`, {
      headers: { authorization: `Bearer ${token}`, accept: 'application/vnd.github+json' },
    });
    if (!response.ok) throw new Error(`fallback comparison is unavailable: GitHub ${response.status}`);
    const body = await response.json();
    const listed = body.files ?? [];
    if (listed.length >= 300 || body.truncated === true) throw new Error('fallback comparison is incomplete; the new budget does not apply to this harness');
    return listed.flatMap((file) => [file.filename, file.previous_filename].filter(Boolean));
  }
}
