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
  const reporter = fileURLToPath(new URL('./quarantine-discovery.mjs', import.meta.url));
  const inventory = JSON.parse(execFileSync(process.execPath, [cli, 'test', ...discovery, '--list', '--reporter=' + reporter], { env, encoding: 'utf8', maxBuffer: 10_000_000 }));
  const entries = quarantine();
  const filters = new Set();
  const summary = [];
  const flat = test => [test.project, test.spec, ...test.titles].join(' ');
  const ambiguous = test => inventory.some(candidate => (flat(candidate) === flat(test)
    && JSON.stringify([candidate.project, candidate.spec, candidate.titles]) !== JSON.stringify([test.project, test.spec, test.titles])) || candidate.tags.length);
  for (const test of inventory) {
    const issue = quarantined(test, entries, inventory);
    if (!issue) continue;
    const group = test.serial ? inventory.filter(candidate => candidate.project === test.project && candidate.spec === test.spec && candidate.serial === test.serial) : [test];
    if (group.some(ambiguous)) {
      summary.push(`- Warning: cannot exclude ambiguous or tagged identity ${test.spec} › ${test.titles.join(' › ')}`);
      continue;
    }
    for (const member of group) {
      const filter = '^' + flat(member).replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '$';
      if (filters.has(filter)) continue;
      filters.add(filter);
      summary.push(`- Skipped [${member.spec} › ${member.titles.join(' › ')}](${issue.url}) [${member.project}]${test.serial ? ' (whole serial group)' : ''}`);
    }
  }
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `\n### Quarantine exclusions\n${summary.join('\n') || 'No unambiguous matching tests.'}\n`);
  env.JOURNEY_QUARANTINE_GREP = JSON.stringify([...filters]);
}
const child = spawn(process.execPath, [cli, 'test', ...args], { env, stdio: 'inherit' });
const result = await new Promise(resolve => child.once('exit', (code, signal) => resolve({ code, signal })));
if (result.signal) process.kill(process.pid, result.signal);
else process.exitCode = result.code ?? 1;
