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

export function appInput(file) {
  if (file === '.github/workflows/production-journey.yml') return false;
  if (file.startsWith('e2e/') && file !== 'e2e/package.json' && file !== 'e2e/package-lock.json') return false;
  return true;
}

export function literalMs(expr) {
  const factors = expr.trim().split('*').map((part) => Number(part.trim().replaceAll('_', '')));
  if (!factors.length || factors.some((factor) => !Number.isSafeInteger(factor))) return null;
  return factors.reduce((total, factor) => total * factor, 1);
}

export function legacyCaps(providerSource, configSource) {
  const run = /const runMs = ([0-9][0-9_\s*]*)/.exec(providerSource);
  const suite = /globalTimeout: production \? ([0-9][0-9_\s*]*) :/.exec(configSource);
  if (!run || !suite) return null;
  const runMs = literalMs(run[1]);
  const suiteMs = literalMs(suite[1]);
  if (!Number.isSafeInteger(runMs) || !Number.isSafeInteger(suiteMs)) return null;
  return { runMs, suiteMs };
}

export function journeySource(inner, required, files) {
  if (inner.runMs >= required.runMs && inner.suiteMs >= required.suiteMs) return { source: 'released', inputs: [] };
  const inputs = files.filter(appInput);
  if (!inputs.length) return { source: 'workflow', inputs };
  return { source: 'blocked', inputs };
}

export function productionSchedule(phase, skips = observedCooldowns) {
  const readyAt = preflightMs + readyzBeforeCalls * readyzRequestMs;
  const slotPhase = ((accountPeriod - phase) % accountPeriod + accountPeriod) % accountPeriod;
  let first = phase;
  if (first < readyAt) first += Math.ceil((readyAt - first) / accountPeriod) * accountPeriod;
  const fiftiethStart = first + (productionAccountCreates - 1 + skips) * accountPeriod;
  let slotAt = Math.ceil((slotPhase + fiftiethStart + requestMs) / slotMs) * slotMs;
  if (slotAt % accountPeriod === 0) slotAt += slotMs;
  const tokenAt = slotAt - slotPhase;
  const pollDone = tokenAt + requestMs + mailPollMs;
  return { phase, readyAt, firstCreate: first, fiftiethStart, tokenAt, pollDone, skips };
}

let cached;
function coverage() {
  if (cached) return cached;
  const period = accountPeriod;
  for (let duration = 1; duration <= cooldownMs; duration += 1) {
    if (Math.ceil(duration / period) !== 1) throw new Error(`cooldown ${duration}ms blocks more than one account slot`);
  }
  const sample = productionSchedule(23_000);
  if (sample.readyAt !== 164_000 || sample.firstCreate !== 233_000 || sample.fiftiethStart !== 3_803_000 || sample.tokenAt !== 3_824_000 || sample.pollDone !== 3_959_000) {
    throw new Error(`23s phase first=${sample.firstCreate} fiftieth=${sample.fiftiethStart} token=${sample.tokenAt} poll=${sample.pollDone}`);
  }
  let worst = sample;
  for (let phase = 0; phase < period; phase += 1) {
    const row = phase === sample.phase ? sample : productionSchedule(phase);
    if (row.pollDone > worst.pollDone) worst = row;
  }
  cached = { sample, worst };
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
  const { sample, worst } = coverage();
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
    fiftiethStart: worst.fiftiethStart,
    pollDone: worst.pollDone,
    samplePollDone: sample.pollDone,
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
