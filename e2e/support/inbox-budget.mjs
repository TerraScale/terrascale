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

export function dnsHostCount(hostnames) {
  if (typeof hostnames !== 'string') throw new Error('inbox budget needs the production hostname list');
  const count = hostnames.split('\n').filter((line) => line.endsWith('.terrascale.tech')).length;
  if (!count) throw new Error('inbox budget needs the production hostname list');
  return count;
}

const headerCooldownMs = 60_000;
const detailAfterSignupMs = 112_000;
const signupCount = 4;

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

export function productionSchedule(phase) {
  const origin = phase === 0 ? 0 : accountPeriod - phase;
  const held = new Set();
  const rel = (abs) => abs - origin;
  const account = (at) => take(at, accountStride, at, Number.POSITIVE_INFINITY, true, held);
  const other = (at) => take(at, 1, at, Number.POSITIVE_INFINITY, false, held);
  const first = account(origin);
  const creates = [first];
  const signups = [];
  for (let index = 1; index < productionAccountCreates; index += 1) {
    const created = creates[creates.length - 1];
    if (index > productionAccountCreates - 1 - signupCount) {
      const response = created + requestMs;
      const token = other(response);
      const signed = token + requestMs;
      const detail = signed + detailAfterSignupMs;
      signups.push({ created: rel(created), token: rel(token), signed: rel(signed), detail: rel(detail) });
      creates.push(account(detail));
    } else {
      creates.push(account(created + 1));
    }
  }
  const fiftieth = creates[creates.length - 1];
  const retryAt = fiftieth + headerCooldownMs;
  const retried = account(retryAt);
  const token = other(retried + requestMs);
  const pollDone = rel(token + requestMs + mailPollMs);
  return {
    phase,
    firstCreate: rel(first),
    fiftiethStart: rel(fiftieth),
    retryAt: rel(retried),
    tokenAt: rel(token),
    pollDone,
    signups,
  };
}

let cached;
function coverage() {
  if (cached) return cached;
  const serial = productionSchedule(23_999);
  if (serial.firstCreate !== 23_999 || serial.fiftiethStart !== 4_013_999 || serial.retryAt !== 4_083_999 || serial.tokenAt !== 4_104_999 || serial.pollDone !== 4_239_999) {
    throw new Error(`serial finish first=${serial.firstCreate} create=${serial.fiftiethStart} retry=${serial.retryAt} token=${serial.tokenAt} poll=${serial.pollDone}`);
  }
  for (const signup of serial.signups) {
    const span = signup.detail - signup.created;
    if (span !== 148_000) throw new Error(`signup detail at +${span}`);
    if (signup.token - signup.created !== 21_000 || signup.signed - signup.created !== 36_000) throw new Error('signup token clock moved');
  }
  cached = { serial };
  return cached;
}

export function proveAllocator() {
  const supported = coverage().serial;
  const retry = tokenRetry(supported);
  if (retry.tokenAt !== 4_167_999 || retry.pollDone !== 4_302_999 || retry.fits) {
    throw new Error(`token retry token=${retry.tokenAt} poll=${retry.pollDone} fits=${retry.fits}`);
  }
  const held = new Set([100, 101, 102]);
  const claimed = take(100 * slotMs, 1, 100 * slotMs, 120 * slotMs, false, held);
  if (claimed !== 103 * slotMs) throw new Error(`occupied claims reached ${claimed}`);
  const now = 1_000_000;
  const day = 86_400_000;
  if (now + day < now + preflightMs) throw new Error('a day-long cooldown is inside the preflight deadline');
  const late = productionSchedule(69_999);
  if (late.pollDone <= supported.pollDone) throw new Error('phase 69999 was treated as supported');
  return { supported: supported.pollDone, retry: retry.pollDone, overBudgetPhase: 69_999 };
}

export function productionInboxRunMs() {
  return coverage().serial.pollDone;
}

export function tokenRetry(schedule = coverage().serial) {
  const origin = schedule.phase === 0 ? 0 : accountPeriod - schedule.phase;
  const held = new Set();
  const claimed = origin + schedule.tokenAt;
  held.add(claimed / slotMs);
  const again = take(claimed + headerCooldownMs, 1, claimed + headerCooldownMs, claimed + providerRetryMs, false, held);
  const tokenAt = again - origin;
  return { tokenAt, pollDone: tokenAt + requestMs + mailPollMs, fits: tokenAt + requestMs + mailPollMs <= productionInboxRunMs() };
}

export function productionSuiteMs() {
  return productionInboxRunMs() + teardownMs;
}

export function productionStepMs(hosts) {
  return shellStarts * requestMs + quarantineMs + hosts * requestMs + preflightMs + readyzBeforeCalls * readyzRequestMs + productionSuiteMs() + readyzAfterCalls * readyzRequestMs;
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
  const { serial } = coverage();
  const runMs = serial.pollDone;
  const suiteMs = productionSuiteMs();
  const stepMinutes = productionStepMinutes(hosts);
  const jobMinutes = productionJobMinutes(stepMinutes);
  const budget = {
    runMs,
    suiteMs,
    stepMinutes,
    jobMinutes,
    preflightMs,
    quarantineMs,
    phase: serial.phase,
    fiftiethStart: serial.fiftiethStart,
    retryAt: serial.retryAt,
    tokenAt: serial.tokenAt,
    pollDone: serial.pollDone,
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
