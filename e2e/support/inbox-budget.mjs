import { writeFileSync } from 'node:fs';

export const slotMs = 7_000;
export const accountStride = 10;
export const productionAccountCreates = 50;
export const mailPollMs = 120_000;
export const teardownMs = 5 * 60_000;
export const requestMs = 15_000;
export const providerRetryMs = 120_000;
export const quarantineMs = 65_000;
export const readyzRequestMs = 15_000;
export const readyzBeforeCalls = 2;
export const readyzAfterCalls = 2;
export const shellStarts = 2;
export const checkoutMinutes = 2;
export const checkoutCount = 1;
export const toolSetupMinutes = 2;
export const toolSetupCount = 3;
export const installMinutes = 15;
export const scrubMinutes = 5;
export const uploadMinutes = 2;
export const uploadCount = 2;
export const runnerCount = 2;
export const identifyMinutes = 2;
export const nonAccountWaitMs = 2 * slotMs;
export const preflightMs = nonAccountWaitMs + providerRetryMs;
export const ordinaryTestMs = 60_000 + mailPollMs;
export const postPollTests = 1;
export const databaseDeletionTests = 4;
export const sdkGroupBTests = 5;
export const sdkPhaseMs = (postPollTests + databaseDeletionTests + sdkGroupBTests) * ordinaryTestMs;

const workflowKeys = [
  'PRODUCTION_CHECKOUT_MINUTES',
  'PRODUCTION_TOOL_MINUTES',
  'PRODUCTION_INSTALL_MINUTES',
  'PRODUCTION_SCRUB_MINUTES',
  'PRODUCTION_UPLOAD_MINUTES',
  'PRODUCTION_STEP_MINUTES',
  'PRODUCTION_JOB_MINUTES',
];

const postCount = checkoutCount + toolSetupCount;
const accountPeriod = accountStride * slotMs;
const headerCooldownMs = 60_000;
const earlyCreates = 41;
const finishTail = ['member', 'dotnet', 'invite', 'waitlist', 'waitlist', 'go', 'python', 'js', 'rust'];
const polled = new Set(['member', 'dotnet', 'go', 'python', 'js', 'rust']);

export function dnsHostCount(hostnames) {
  if (typeof hostnames !== 'string') throw new Error('inbox budget needs the production hostname list');
  const count = hostnames.split('\n').filter((line) => line.endsWith('.terrascale.tech')).length;
  if (!count) throw new Error('inbox budget needs the production hostname list');
  return count;
}

export function phaseDelay(now) {
  const into = now % accountPeriod;
  return into === 0 ? 0 : accountPeriod - into;
}

export function sendAfterWake(wake, deadline) {
  return wake <= deadline;
}

function take(now, every, notBefore, deadline, accounts, held) {
  let slot = every * Math.ceil(Math.max(now, notBefore) / (every * slotMs));
  for (;;) {
    if (slot * slotMs >= deadline) return undefined;
    if (accounts ? slot % accountStride !== 0 : slot % accountStride === 0) {
      slot += every;
      continue;
    }
    if (held.has(slot)) {
      slot += every;
      continue;
    }
    held.add(slot);
    return slot * slotMs;
  }
}

function claim(at, every, accounts, held) {
  const taken = take(at, every, at, Number.POSITIVE_INFINITY, accounts, held);
  if (taken === undefined) throw new Error('allocator slot missing');
  return taken;
}

export function productionSchedule(phase) {
  if (!Number.isInteger(phase) || phase < 0 || phase >= accountPeriod) throw new Error(`phase ${phase} is outside the account period`);
  const origin = phase === 0 ? 0 : accountPeriod - phase;
  const held = new Set();
  const rel = (abs) => abs - origin;
  let cursor = origin;
  let first = origin;
  for (let index = 0; index < earlyCreates; index += 1) {
    const created = claim(index === 0 ? origin : cursor + 1, accountStride, true, held);
    if (index === 0) first = created;
    cursor = created;
  }
  const steps = [];
  for (const name of finishTail) {
    const created = claim(cursor + 1, accountStride, true, held);
    const occupied = claim(created + requestMs, 1, false, held);
    const token = claim(created + requestMs, 1, false, held);
    const poll = polled.has(name);
    const done = token + requestMs + (poll ? mailPollMs : 0);
    steps.push({ name, created: rel(created), occupied: rel(occupied), token: rel(token), done: rel(done), poll, tokenAbs: token });
    cursor = done;
  }
  const last = steps[steps.length - 1];
  return { phase, origin, firstCreate: rel(first), pollDone: last.done, tokenAt: last.token, tokenAbs: last.tokenAbs, steps };
}

function assertAdmitted(schedule) {
  if (earlyCreates + finishTail.length !== productionAccountCreates) throw new Error('admitted creates are not the production coverage');
  if (schedule.steps.length !== finishTail.length) throw new Error('finish tail moved');
  if (schedule.firstCreate !== schedule.phase) throw new Error(`first account wait ${schedule.firstCreate} is not phase ${schedule.phase}`);
  const member = schedule.steps[0];
  const dotnet = schedule.steps[1];
  if (member.name !== 'member' || !member.poll || dotnet.name !== 'dotnet' || !dotnet.poll) throw new Error('member poll is not before the SDK signup');
  if (dotnet.created < member.done) throw new Error('SDK account starts before the member poll ends');
  for (const step of schedule.steps) {
    if (step.token < step.occupied + slotMs) throw new Error(`${step.name} token was an uncontended slot`);
    const span = step.poll ? requestMs + mailPollMs : requestMs;
    if (step.done - step.token !== span) throw new Error(`${step.name} window is ${step.done - step.token}, not ${span}`);
  }
  const last = schedule.steps[schedule.steps.length - 1];
  if (last.name !== 'rust' || !last.poll || schedule.pollDone !== last.done) throw new Error('the funded poll is not the last SDK poll');
}

let funded;
let worstPhase = 0;
export function fundedMailboxMs() {
  if (funded !== undefined) return funded;
  let max = 0;
  for (let phase = 0; phase < accountPeriod; phase += 1) {
    const schedule = productionSchedule(phase);
    assertAdmitted(schedule);
    if (schedule.pollDone > max) {
      max = schedule.pollDone;
      worstPhase = phase;
    }
  }
  funded = max;
  return funded;
}

export function writeEnvelope(file, now = Date.now()) {
  const schedule = productionSchedule(phaseDelay(now));
  assertAdmitted(schedule);
  if (schedule.pollDone > fundedMailboxMs()) throw new Error('admitted phase exceeds the funded mailbox window');
  const deadline = now + schedule.pollDone;
  writeFileSync(file, String(deadline));
  return { phase: schedule.phase, pollDone: schedule.pollDone, deadline };
}

export function tokenRetry(schedule) {
  const held = new Set([schedule.tokenAbs / slotMs]);
  const again = take(schedule.tokenAbs + headerCooldownMs, 1, schedule.tokenAbs + headerCooldownMs, schedule.tokenAbs + providerRetryMs, false, held);
  if (again === undefined) return { tokenAt: undefined, pollDone: undefined, fits: false };
  const tokenAt = again - schedule.origin;
  const pollDone = tokenAt + requestMs + mailPollMs;
  return { tokenAt, pollDone, fits: pollDone <= schedule.pollDone };
}

export function envelopeFailure(message) {
  return typeof message === 'string' && (message.includes('is over budget') || message.includes("has no slot before the run's time limit"));
}

export function journeyStatus(status, failureCount, quarantinedCount, envelopeCount) {
  if (envelopeCount > 0) return 'failed';
  if (status === 'failed' && failureCount > 0 && failureCount === quarantinedCount) return 'passed';
  return status;
}

export function reportLines({ event, suite, verified, terrabase }) {
  const lines = [suite ? `Suite: ${suite}.` : 'Suite: not selected.'];
  if (event === 'workflow_run') {
    lines.push(verified ? `Deploy revision: ${verified}.` : 'Deploy revision: not identified.');
    lines.push(terrabase ? `TerraBase release: ${terrabase}.` : 'TerraBase release: not identified.');
  }
  return lines;
}

export function proveAllocator() {
  const fundedMs = fundedMailboxMs();
  const worst = productionSchedule(worstPhase);
  const retry = tokenRetry(worst);
  if (retry.fits || retry.pollDone === undefined || retry.pollDone <= worst.pollDone) {
    throw new Error(`token retry token=${retry.tokenAt} poll=${retry.pollDone} fits=${retry.fits}`);
  }
  const open = productionSchedule(0);
  const openRetry = tokenRetry(open);
  if (openRetry.fits || open.pollDone > fundedMs) throw new Error('phase 0 was over the funded window or its token retry fit');
  const held = new Set([100, 101, 102]);
  const claimed = take(100 * slotMs, 1, 100 * slotMs, 120 * slotMs, false, held);
  if (claimed !== 103 * slotMs) throw new Error(`occupied claims reached ${claimed}`);
  const day = 86_400_000;
  if (day <= preflightMs) throw new Error('a day-long cooldown is inside the preflight deadline');
  if (sendAfterWake(15_000, 14_000)) throw new Error('a wake at 15s passed a 14s first-claim cutoff');
  if (!sendAfterWake(13_000, 14_000)) throw new Error('a wake at 13s missed a 14s first-claim cutoff');
  if (15_000 >= 13_000 + slotMs) throw new Error('the late wake was outside the slot, not the cutoff');
  if (journeyStatus('failed', 1, 1, 1) !== 'failed') throw new Error('an envelope failure was quarantined into a pass');
  if (journeyStatus('failed', 1, 1, 0) !== 'passed') throw new Error('an ordinary quarantined failure stayed failed');
  if (journeyStatus('failed', 2, 1, 0) !== 'failed') throw new Error('a partial quarantine passed');
  const sha = '0123456789abcdef0123456789abcdef01234567';
  const selected = reportLines({ event: 'workflow_dispatch', suite: sha, verified: '', terrabase: '' });
  if (selected.join('\n') !== `Suite: ${sha}.`) throw new Error('a selected suite grew a release line');
  const missing = reportLines({ event: 'workflow_run', suite: '', verified: '', terrabase: '' });
  if (missing.join('\n') !== 'Suite: not selected.\nDeploy revision: not identified.\nTerraBase release: not identified.') {
    throw new Error('missing release evidence was labeled as a suite');
  }
  const both = reportLines({ event: 'workflow_run', suite: sha, verified: sha, terrabase: sha });
  if (both.join('\n') !== `Suite: ${sha}.\nDeploy revision: ${sha}.\nTerraBase release: ${sha}.`) throw new Error('verified release lines moved');
  if (sdkPhaseMs !== 1_800_000 || teardownMs !== 300_000) throw new Error('sdk or cleanup phase moved');
  return { funded: fundedMs, worstPhase, retry: retry.pollDone, sdkPhaseMs, teardownMs };
}

export function productionInboxRunMs() {
  return fundedMailboxMs();
}

export function productionSuiteMs() {
  if (process.env.PRODUCTION_PHASE === 'cleanup') return teardownMs;
  return fundedMailboxMs() + sdkPhaseMs;
}

export function productionStepMs(hosts) {
  return shellStarts * requestMs + quarantineMs + hosts * requestMs + preflightMs + readyzBeforeCalls * readyzRequestMs + fundedMailboxMs() + sdkPhaseMs + teardownMs + readyzAfterCalls * readyzRequestMs;
}

export function productionStepMinutes(hosts) {
  return Math.ceil(productionStepMs(hosts) / 60_000);
}

export function productionJobMinutes(stepMinutes) {
  return checkoutMinutes * (checkoutCount + runnerCount + postCount)
    + identifyMinutes
    + toolSetupMinutes * toolSetupCount
    + installMinutes
    + stepMinutes
    + scrubMinutes
    + uploadMinutes * uploadCount;
}

function integer(env, key) {
  const raw = env[key];
  if (typeof raw !== 'string' || !/^[1-9][0-9]*$/.test(raw)) {
    throw new Error(`running workflow budget ${key}=${raw} is not a positive integer`);
  }
  const value = Number(raw);
  if (!Number.isSafeInteger(value)) {
    throw new Error(`running workflow budget ${key}=${raw} is outside the safe integer range`);
  }
  return value;
}

export function assertRunningBudget(env = process.env, hostnames) {
  const hosts = dnsHostCount(hostnames);
  const runMs = fundedMailboxMs();
  const suiteMs = runMs + sdkPhaseMs;
  const stepMinutes = productionStepMinutes(hosts);
  const jobMinutes = productionJobMinutes(stepMinutes);
  const budget = {
    runMs,
    suiteMs,
    stepMinutes,
    jobMinutes,
    preflightMs,
    quarantineMs,
    sdkPhaseMs,
    teardownMs,
    worstPhase,
    pollDone: runMs,
    hosts,
  };
  const ci = env.GITHUB_ACTIONS === 'true';
  const present = workflowKeys.filter((key) => key in env);
  if (!ci && !present.length) return budget;
  const missing = workflowKeys.filter((key) => !(key in env));
  if (missing.length) throw new Error(`running workflow budget missing ${missing.join(' ')}`);
  const checkout = integer(env, 'PRODUCTION_CHECKOUT_MINUTES');
  const tool = integer(env, 'PRODUCTION_TOOL_MINUTES');
  const install = integer(env, 'PRODUCTION_INSTALL_MINUTES');
  const scrub = integer(env, 'PRODUCTION_SCRUB_MINUTES');
  const upload = integer(env, 'PRODUCTION_UPLOAD_MINUTES');
  const step = integer(env, 'PRODUCTION_STEP_MINUTES');
  const job = integer(env, 'PRODUCTION_JOB_MINUTES');
  const needJob = productionJobMinutes(step);
  if (!Number.isSafeInteger(needJob) || checkout !== checkoutMinutes || tool !== toolSetupMinutes || install !== installMinutes || scrub !== scrubMinutes || upload !== uploadMinutes || step < stepMinutes || job < needJob) {
    throw new Error(`running workflow budget checkout=${env.PRODUCTION_CHECKOUT_MINUTES} tool=${env.PRODUCTION_TOOL_MINUTES} install=${env.PRODUCTION_INSTALL_MINUTES} scrub=${env.PRODUCTION_SCRUB_MINUTES} upload=${env.PRODUCTION_UPLOAD_MINUTES} step=${env.PRODUCTION_STEP_MINUTES} job=${env.PRODUCTION_JOB_MINUTES}; need checkout ${checkoutMinutes} for ${checkoutCount} checkouts, tool ${toolSetupMinutes} for ${toolSetupCount} actions, install ${installMinutes}, scrub ${scrubMinutes}, upload ${uploadMinutes} for ${uploadCount} artifacts, step at least ${stepMinutes}, job at least ${needJob} including ${runnerCount} runner phases and ${postCount} post actions`);
  }
  return budget;
}
