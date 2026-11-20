import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { assertRunningBudget } from './inbox-budget.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const hostnames = readFileSync(join(here, '../env/hostnames'), 'utf8');
const budget = assertRunningBudget(process.env, hostnames);
const release = (process.env.RELEASED_SHA || execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' })).trim();
console.log(`production journey release=${release} providerMs=${budget.runMs} suiteMs=${budget.suiteMs} pollDone=${budget.pollDone} legacy=false`);
