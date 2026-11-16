import { execFileSync } from 'node:child_process';
import { appendFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';

const prefix = 'Flaky journey test: ';
const label = 'journey-quarantine';
const repo = process.env.GITHUB_REPOSITORY;
const summary = text => process.env.GITHUB_STEP_SUMMARY && appendFileSync(process.env.GITHUB_STEP_SUMMARY, text + '\n');
const escape = text => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const selector = title => /^([\w/-]+\.spec\.ts)(?: › (.+))?$/.exec(title.slice(prefix.length));

async function api(path, body) {
  const response = await fetch(`https://api.github.com/repos/${repo}/${path}`, {
    method: body ? 'POST' : 'GET',
    headers: { authorization: `Bearer ${process.env.GITHUB_TOKEN}`, accept: 'application/vnd.github+json' },
    ...(body ? { body: JSON.stringify(body) } : {}),
    redirect: 'follow',
  });
  if (!response.ok) throw new Error(`GitHub ${response.status}: ${path}`);
  if (response.headers.get('link')?.includes('rel="next"')) throw new Error(`GitHub list exceeds one page: ${path}`);
  return response;
}

async function issues() {
  return (await (await api(`issues?state=open&labels=${label}&per_page=100`)).json())
    .filter(issue => !issue.pull_request && issue.title.startsWith(prefix) && selector(issue.title))
    .map(issue => ({ number: issue.number, name: issue.title.slice(prefix.length), url: issue.html_url }));
}

export function quarantine() {
  return process.env.JOURNEY_QUARANTINE ? JSON.parse(readFileSync(process.env.JOURNEY_QUARANTINE, 'utf8')) : [];
}

export function quarantineGrep() {
  if (process.env.GITHUB_EVENT_NAME !== 'pull_request') return undefined;
  return quarantine().map(issue => {
    const [spec, ...titles] = issue.name.split(' › ');
    return new RegExp(`(?:^| )${escape(spec)}${titles.length ? ` ${escape(titles.join(' '))}(?: @\\S+)*$` : '(?: |$)'}`);
  });
}

export function quarantined(test, entries = quarantine()) {
  return entries.find(issue => issue.name === test.spec || issue.name === `${test.spec} › ${test.title}`);
}

async function read() {
  let entries = [];
  try { entries = await issues(); }
  catch (error) { summary(`Warning: ${error.message}; quarantining nothing.`); }
  mkdirSync(dirname(process.env.JOURNEY_QUARANTINE), { recursive: true });
  writeFileSync(process.env.JOURNEY_QUARANTINE, JSON.stringify(entries));
  summary(`## Quarantine\n${entries.length ? entries.map(issue => `- [${issue.name}](${issue.url}) — ${process.env.GITHUB_EVENT_NAME === 'pull_request' ? 'skipped on this PR' : 'run and report'}`).join('\n') : 'No open quarantine entries.'}`);
}

async function reports(run) {
  const artifacts = (await (await api(`actions/runs/${run.id}/artifacts?per_page=100`)).json()).artifacts;
  const result = [];
  for (const artifact of artifacts.filter(item => /^journey-results-\d+-/.test(item.name) && !item.expired)) {
    if (artifact.size_in_bytes > 10_000_000) throw new Error('Journey result artifact is too large');
    const archive = Buffer.from(await (await api(`actions/artifacts/${artifact.id}/zip`)).arrayBuffer());
    const directory = mkdtempSync(join(tmpdir(), 'journey-results-'));
    const zip = join(directory, 'results.zip');
    try {
      writeFileSync(zip, archive, { mode: 0o600 });
      const files = execFileSync('unzip', ['-Z1', zip], { encoding: 'utf8' }).trim().split('\n');
      for (const file of files.filter(name => /^[0-9a-f][0-9a-f-]{35}\.json$/.test(name))) {
        const report = JSON.parse(execFileSync('unzip', ['-p', zip, file], { encoding: 'utf8', maxBuffer: 10_000_000 }));
        const attempt = Number(artifact.name.split('-')[2]);
        if (report.run !== String(run.id) || report.attempt !== attempt) throw new Error('Journey result identity mismatch');
        for (const test of report.tests) {
          if (!/^[\w/-]+\.spec\.ts$/.test(test.spec) || test.spec.split('/').includes('..') || typeof test.title !== 'string' || test.title.length > 1000) throw new Error('Invalid journey test identity');
          result.push({ ...test, attempt });
        }
      }
    } finally { rmSync(directory, { recursive: true, force: true }); }
  }
  return result;
}

async function reconcile() {
  const run = JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8')).workflow_run;
  if (run.head_repository.full_name !== repo) return;
  const entries = await issues();
  const results = await reports(run);
  const link = attempt => `${run.html_url}/attempts/${attempt}`;
  const current = results.filter(test => test.attempt === run.run_attempt);
  const failures = new Map();
  for (const test of current.filter(test => test.status === 'unexpected')) {
    const issue = quarantined(test, entries);
    if (issue) failures.set(issue.number, issue);
  }
  for (const issue of failures.values()) await api(`issues/${issue.number}/comments`, { body: `Quarantined journey failure: ${issue.name}\n\n${link(run.run_attempt)}` });
  if (run.name !== 'CI' || run.conclusion !== 'success' || run.run_attempt < 2) return;
  const changed = new Set();
  const prs = run.pull_requests.length || run.event !== 'pull_request' ? run.pull_requests
    : (await (await api(`commits/${run.head_sha}/pulls?per_page=100`)).json()).filter(pr => pr.head.sha === run.head_sha);
  if (run.event === 'pull_request' && !prs.length) throw new Error('Cannot identify the PR to check its changed specs');
  for (const pr of prs) {
    for (const file of await (await api(`pulls/${pr.number}/files?per_page=100`)).json()) changed.add(file.filename);
  }
  const flakes = new Map();
  for (const test of results.filter(test => test.attempt < run.run_attempt && test.status === 'unexpected')) {
    if (changed.has(`e2e/${test.spec}`)) continue;
    if (current.some(pass => pass.spec === test.spec && pass.title === test.title && pass.project === test.project && pass.status === 'expected')) flakes.set(`${test.spec} › ${test.title}`, test);
  }
  for (const [name, test] of flakes) {
    const body = `Failed, then passed on a rerun of commit ${run.head_sha}.\n\nFailing: ${link(test.attempt)}\nPassing: ${link(run.run_attempt)}\n\nFix the root cause and close this issue to leave quarantine.`;
    const issue = quarantined(test, entries);
    if (issue) await api(`issues/${issue.number}/comments`, { body });
    else {
      const entry = (prefix + name).length > 256 ? test.spec : name;
      const created = await (await api('issues', { title: prefix + entry, labels: [label], body })).json();
      entries.push({ name: entry, number: created.number, url: created.html_url });
    }
  }
}

if (/[\\/]support[\\/]quarantine\.mjs$/.test(process.argv[1] ?? '')) {
  const operation = process.argv[2] === 'read' ? read : process.argv[2] === 'reconcile' ? reconcile : undefined;
  if (!operation) throw new Error('Expected read or reconcile');
  operation().catch(error => { console.error(error); process.exitCode = 1; });
}
