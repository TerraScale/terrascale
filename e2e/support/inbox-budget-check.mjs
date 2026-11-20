import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { assertRunningBudget, phaseDelay, productionSchedule, proveAllocator, writeEnvelope } from './inbox-budget.mjs';
import { acquireOwner, releaseOwner } from './production-owner.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const hostnames = readFileSync(join(here, '../env/hostnames'), 'utf8');
const budget = assertRunningBudget(process.env, hostnames);
const proof = proveAllocator();
const now = 10_000_000;
const envelope = join(mkdtempSync(join(tmpdir(), 'inbox-envelope-')), 'inbox-envelope');
const written = writeEnvelope(envelope, now);
const admitted = productionSchedule(phaseDelay(now));
if (written.deadline !== now + admitted.pollDone || readFileSync(envelope, 'utf8') !== String(written.deadline)) {
  throw new Error('startup envelope is not the admitted phase');
}
const root = mkdtempSync(join(tmpdir(), 'inbox-owner-'));
const state = join(root, 'state');
acquireOwner(state);
const cooldown = join(state, 'inbox-cooldowns');
mkdirSync(cooldown);
const future = String(Date.now() + 86_400_000);
writeFileSync(join(cooldown, future), '');
let blocked = false;
try {
  acquireOwner(state);
} catch (error) {
  blocked = error.message === 'production state is owned by another run';
}
if (!blocked || !existsSync(join(cooldown, future))) throw new Error('a live owner lost its cooldown');
releaseOwner(state);
if (existsSync(`${state}.owner`)) throw new Error('the owner release left the lock');
const dead = join(root, 'dead');
mkdirSync(`${dead}.owner`);
writeFileSync(join(`${dead}.owner`, 'pid'), '2147483647');
mkdirSync(join(dead, 'inbox-cooldowns'), { recursive: true });
const kept = String(Date.now() + 86_400_000);
writeFileSync(join(dead, 'inbox-cooldowns', kept), '');
acquireOwner(dead);
if (!existsSync(join(dead, 'inbox-cooldowns', kept))) throw new Error('a dead owner dropped an active cooldown');
const suite = execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim();
console.log(`production journey suite=${suite} providerMs=${budget.runMs} suiteMs=${budget.suiteMs} pollDone=${budget.pollDone} worstPhase=${proof.worstPhase} retryPoll=${proof.retry} sdkPhaseMs=${proof.sdkPhaseMs} cleanupMs=${proof.teardownMs} legacy=false`);
