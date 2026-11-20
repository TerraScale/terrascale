import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

export const slotMs = 7_000;
export const accountStride = 10;
export const productionAccountCreates = 50;
export const observedCooldowns = 2;
export const cooldownMs = 60_000;
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
export const checkoutCount = 2;
export const toolSetupMinutes = 2;
export const toolSetupCount = 3;
export const installMinutes = 15;
export const scrubMinutes = 5;
export const uploadMinutes = 2;
export const uploadCount = 2;
export const runnerCount = 2;
export const nonAccountWaitMs = 2 * slotMs;
export const preflightMs = nonAccountWaitMs + providerRetryMs;
export const reviewerPreflightMs = cooldownMs + slotMs + requestMs;

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
const dnsHosts = readFileSync(join(dirname(fileURLToPath(import.meta.url)), '../env/hostnames'), 'utf8')
  .split('\n')
  .filter((line) => line.endsWith('.terrascale.tech')).length;

function nextNonAccount(after, alignment, every) {
  let at = Math.ceil(after / slotMs) * slotMs;
  if (at >= alignment && (at - alignment) % every === 0) at += slotMs;
  return at;
}

export function productionSchedule(preflight) {
  const alignment = (accountStride - 1) * slotMs;
  const every = accountStride * slotMs;
  const readyAt = preflight + readyzBeforeCalls * readyzRequestMs;
  let index = 0;
  while (alignment + index * every < readyAt) index += 1;
  const skips = observedCooldowns * Math.ceil(cooldownMs / every);
  const creates = [];
  for (let n = 0; n < productionAccountCreates; n += 1) {
    creates.push(alignment + (index + n + (n === 0 ? 0 : skips)) * every);
  }
  const fiftiethStart = creates[creates.length - 1];
  const tokenAt = nextNonAccount(fiftiethStart + requestMs, alignment, every);
  const pollDone = tokenAt + requestMs + mailPollMs;
  return { alignment, readyAt, firstCreate: creates[0], fiftiethStart, tokenAt, pollDone, skips, creates };
}

function fundedSchedule() {
  const reviewer = productionSchedule(reviewerPreflightMs);
  if (reviewer.fiftiethStart !== 3_703_000) {
    throw new Error(`reviewer schedule starts create 50 at ${reviewer.fiftiethStart}, expected 3703000`);
  }
  const actual = productionSchedule(preflightMs);
  if (actual.pollDone < reviewer.pollDone) {
    throw new Error(`funded poll ends at ${actual.pollDone}, before the reviewer poll at ${reviewer.pollDone}`);
  }
  return { reviewer, actual };
}

export function productionInboxRunMs() {
  return fundedSchedule().actual.pollDone;
}

export function productionSuiteMs() {
  const { actual } = fundedSchedule();
  return actual.pollDone - actual.readyAt + teardownMs;
}

export function productionStepMs() {
  const runMs = productionInboxRunMs();
  return shellStarts * requestMs + quarantineMs + dnsHosts * requestMs + runMs + teardownMs + readyzAfterCalls * readyzRequestMs;
}

export function productionStepMinutes() {
  return Math.ceil(productionStepMs() / 60_000);
}

export function productionJobMinutes(stepMinutes = productionStepMinutes()) {
  return checkoutMinutes * (checkoutCount + runnerCount + postCount)
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

export function assertRunningBudget(env = process.env) {
  const { reviewer, actual } = fundedSchedule();
  const runMs = actual.pollDone;
  const suiteMs = productionSuiteMs();
  const stepMinutes = productionStepMinutes();
  const jobMinutes = productionJobMinutes(stepMinutes);
  const budget = {
    runMs,
    suiteMs,
    stepMinutes,
    jobMinutes,
    preflightMs,
    quarantineMs,
    fiftiethStart: actual.fiftiethStart,
    reviewerFiftieth: reviewer.fiftiethStart,
    pollDone: actual.pollDone,
    reviewerPollDone: reviewer.pollDone,
    slots: actual.creates.length,
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
