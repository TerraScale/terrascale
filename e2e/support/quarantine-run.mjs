import { execFileSync, spawn } from 'node:child_process';
import { appendFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { quarantine, quarantined } from './quarantine.mjs';

const cli = fileURLToPath(new URL('../node_modules/playwright/cli.js', import.meta.url));
const args = process.argv.slice(2);
const env = { ...process.env };
delete env.JOURNEY_QUARANTINE_GREP;
if (process.env.GITHUB_EVENT_NAME === 'pull_request' && quarantine().length) {
  const discovery = [];
  for (let index = 0; index < args.length; index++) {
    if (['--grep', '-g', '--grep-invert'].includes(args[index])) index++;
    else if (!/^--grep(?:-invert)?=/.test(args[index])) discovery.push(args[index]);
  }
  const report = JSON.parse(execFileSync(process.execPath, [cli, 'test', ...discovery, '--list', '--reporter=json'], { env, encoding: 'utf8', maxBuffer: 10_000_000 }));
  const inventory = [];
  function visit(suite, titles) {
    for (const spec of suite.specs ?? []) for (const test of spec.tests) inventory.push({ spec: spec.file, titles: [...titles, spec.title], tags: spec.tags, project: test.projectName });
    for (const child of suite.suites ?? []) visit(child, [...titles, child.title]);
  }
  for (const suite of report.suites) visit(suite, []);
  const entries = quarantine();
  const filters = [];
  const summary = [];
  const flat = test => [test.project, test.spec, ...test.titles].join(' ');
  for (const test of inventory) {
    const issue = quarantined(test, entries, inventory);
    if (!issue) continue;
    const ambiguous = inventory.some(candidate => (flat(candidate) === flat(test)
      && JSON.stringify([candidate.project, candidate.spec, candidate.titles]) !== JSON.stringify([test.project, test.spec, test.titles])) || candidate.tags.length);
    if (ambiguous) {
      summary.push(`- Warning: cannot exclude ambiguous or tagged identity ${test.spec} › ${test.titles.join(' › ')}`);
      continue;
    }
    filters.push('^' + flat(test).replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '$');
    summary.push(`- Skipped [${test.spec} › ${test.titles.join(' › ')}](${issue.url}) [${test.project}]`);
  }
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `\n### Quarantine exclusions\n${summary.join('\n') || 'No unambiguous matching tests.'}\n`);
  env.JOURNEY_QUARANTINE_GREP = JSON.stringify(filters);
}
const child = spawn(process.execPath, [cli, 'test', ...args], { env, stdio: 'inherit' });
const result = await new Promise(resolve => child.once('exit', (code, signal) => resolve({ code, signal })));
if (result.signal) process.kill(process.pid, result.signal);
else process.exitCode = result.code ?? 1;
