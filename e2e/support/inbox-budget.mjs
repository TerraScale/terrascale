export const slotMs = 7_000;
export const accountStride = 10;
export const productionAccountCreates = 50;
export const observedCooldowns = 2;
export const cooldownMs = 60_000;
export const mailPollMs = 120_000;
export const teardownMs = 5 * 60_000;
export const requestMs = 15_000;
export const quarantineMs = 65_000;
export const readyzRequestMs = 15_000;
export const readyzCalls = 4;
export const checkoutMinutes = 2;
export const toolSetupMinutes = 2;
export const toolSetupCount = 3;
export const installMinutes = 15;
export const scrubMinutes = 5;
export const uploadMinutes = 2;
export const uploadCount = 2;
export const preflightMs = cooldownMs + slotMs + requestMs;

const workflowKeys = [
  'PRODUCTION_CHECKOUT_MINUTES',
  'PRODUCTION_TOOL_MINUTES',
  'PRODUCTION_INSTALL_MINUTES',
  'PRODUCTION_SCRUB_MINUTES',
  'PRODUCTION_UPLOAD_MINUTES',
  'PRODUCTION_STEP_MINUTES',
  'PRODUCTION_JOB_MINUTES',
];

export function productionInboxRunMs(accounts = productionAccountCreates) {
  const alignment = (accountStride - 1) * slotMs;
  const span = (accounts - 1) * accountStride * slotMs;
  const tail = 3 * slotMs + mailPollMs;
  return alignment + span + observedCooldowns * cooldownMs + tail;
}

export function productionSuiteMs(accounts = productionAccountCreates) {
  return productionInboxRunMs(accounts) + teardownMs;
}

export function productionStepMs(accounts = productionAccountCreates) {
  return quarantineMs + preflightMs + readyzCalls * readyzRequestMs + productionSuiteMs(accounts);
}

export function productionStepMinutes(accounts = productionAccountCreates) {
  return Math.ceil(productionStepMs(accounts) / 60_000);
}

export function productionJobMinutes(stepMinutes = productionStepMinutes()) {
  return checkoutMinutes + toolSetupMinutes * toolSetupCount + installMinutes + stepMinutes + scrubMinutes + uploadMinutes * uploadCount;
}

export function accountSlotsWithin(runMs) {
  let count = 0;
  for (let at = (accountStride - 1) * slotMs; at < runMs; at += accountStride * slotMs) count += 1;
  return count;
}

function integer(env, key) {
  const raw = env[key];
  if (typeof raw !== 'string' || !/^[0-9]+$/.test(raw)) {
    throw new Error(`running workflow budget ${key}=${raw} is not an integer`);
  }
  return Number(raw);
}

export function assertRunningBudget(env = process.env) {
  const runMs = productionInboxRunMs();
  const suiteMs = productionSuiteMs();
  const stepMinutes = productionStepMinutes();
  const jobMinutes = productionJobMinutes(stepMinutes);
  const slots = accountSlotsWithin(runMs);
  const createsAfterPreflight = slots - accountSlotsWithin(preflightMs);
  if (createsAfterPreflight < productionAccountCreates) {
    throw new Error(`inbox window holds ${createsAfterPreflight} account slots after preflight for ${productionAccountCreates} creates`);
  }
  const present = workflowKeys.filter((key) => key in env);
  const budget = { runMs, suiteMs, stepMinutes, jobMinutes, preflightMs, quarantineMs, slots };
  if (!present.length) return budget;
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
  if (checkout !== checkoutMinutes || tool !== toolSetupMinutes || install !== installMinutes || scrub !== scrubMinutes || upload !== uploadMinutes || step < stepMinutes || job < needJob) {
    throw new Error(`running workflow budget checkout=${env.PRODUCTION_CHECKOUT_MINUTES} tool=${env.PRODUCTION_TOOL_MINUTES} install=${env.PRODUCTION_INSTALL_MINUTES} scrub=${env.PRODUCTION_SCRUB_MINUTES} upload=${env.PRODUCTION_UPLOAD_MINUTES} step=${env.PRODUCTION_STEP_MINUTES} job=${env.PRODUCTION_JOB_MINUTES}; need checkout ${checkoutMinutes}, tool ${toolSetupMinutes} for ${toolSetupCount} actions, install ${installMinutes}, scrub ${scrubMinutes}, upload ${uploadMinutes} for ${uploadCount} artifacts, step at least ${stepMinutes}, job at least ${needJob}`);
  }
  return budget;
}
