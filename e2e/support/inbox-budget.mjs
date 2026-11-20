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

function nextClaim(at) {
  let claim = Math.ceil(at / slotMs) * slotMs;
  if (claim % accountPeriod === 0) claim += slotMs;
  return claim;
}

function latestClaim(first, limit) {
  let claim = first;
  for (;;) {
    const next = nextClaim(claim + 1);
    if (next > limit) return claim;
    claim = next;
  }
}

export const serialPolls = 4;
export const serialPollMs = 100_000;

export function productionSchedule(phase, misses = serialPolls) {
  const readyAt = preflightMs + readyzBeforeCalls * readyzRequestMs;
  const slotPhase = ((accountPeriod - phase) % accountPeriod + accountPeriod) % accountPeriod;
  let first = phase;
  if (first < readyAt) first += Math.ceil((readyAt - first) / accountPeriod) * accountPeriod;
  const fiftiethStart = first + (productionAccountCreates - 1 + misses) * accountPeriod;
  const open = nextClaim(slotPhase + fiftiethStart + requestMs);
  const limit = open + providerRetryMs - requestMs;
  const claim = latestClaim(open, limit);
  const openAt = open - slotPhase;
  const tokenAt = claim - slotPhase;
  const quietPoll = openAt + requestMs + mailPollMs;
  const pollDone = tokenAt + requestMs + mailPollMs;
  return { phase, readyAt, firstCreate: first, fiftiethStart, openAt, tokenAt, quietPoll, pollDone, misses };
}

let cached;
function coverage() {
  if (cached) return cached;
  const serial = productionSchedule(23_999);
  if (serial.fiftiethStart !== 3_943_999 || serial.openAt !== 3_964_999 || serial.quietPoll !== 4_099_999) {
    throw new Error(`serial finish create=${serial.fiftiethStart} token=${serial.openAt} poll=${serial.quietPoll}`);
  }
  if (serialPollMs <= accountPeriod || serialPollMs > mailPollMs) throw new Error('a 100s poll must miss one 70s slot and stay inside 120s');
  let worst = serial;
  for (let phase = 0; phase < accountPeriod; phase += 1) {
    const row = phase === serial.phase ? serial : productionSchedule(phase);
    if (row.tokenAt < row.openAt || row.pollDone < row.quietPoll) throw new Error(`phase ${phase} shortens the poll`);
    if (row.pollDone - row.tokenAt !== requestMs + mailPollMs) throw new Error(`phase ${phase} clips the poll`);
    if (row.pollDone > worst.pollDone) worst = row;
  }
  if (worst.pollDone < serial.quietPoll) throw new Error('deadline clips the serial poll');
  cached = { serial, worst };
  return cached;
}

export function productionInboxRunMs() {
  return coverage().worst.pollDone;
}

export function productionSuiteMs() {
  const { worst } = coverage();
  return worst.pollDone - worst.readyAt + teardownMs;
}

export function productionStepMs(hosts) {
  return shellStarts * requestMs + quarantineMs + hosts * requestMs + productionInboxRunMs() + teardownMs + readyzAfterCalls * readyzRequestMs;
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
  const { serial, worst } = coverage();
  const runMs = worst.pollDone;
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
    phase: worst.phase,
    fiftiethStart: serial.fiftiethStart,
    openAt: serial.openAt,
    tokenAt: worst.tokenAt,
    quietPoll: serial.quietPoll,
    pollDone: worst.pollDone,
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
