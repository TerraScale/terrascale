export const slotMs = 7_000;
export const accountStride = 10;
export const productionAccountCreates = 50;
export const observedCooldowns = 2;
export const cooldownMs = 60_000;
export const mailPollMs = 120_000;
export const teardownMs = 5 * 60_000;
export const readyzMs = 30_000;
export const installMinutes = 15;
export const scrubMinutes = 5;

export function productionInboxRunMs(accounts = productionAccountCreates) {
  const alignment = (accountStride - 1) * slotMs;
  const span = (accounts - 1) * accountStride * slotMs;
  const tail = 3 * slotMs + mailPollMs;
  return alignment + span + observedCooldowns * cooldownMs + tail;
}

export function productionSuiteMs(accounts = productionAccountCreates) {
  return productionInboxRunMs(accounts) + teardownMs;
}

export function productionStepMinutes(accounts = productionAccountCreates) {
  return Math.ceil((readyzMs + productionSuiteMs(accounts) + readyzMs) / 60_000);
}

export function productionJobMinutes(accounts = productionAccountCreates) {
  return installMinutes + productionStepMinutes(accounts) + scrubMinutes;
}

export function accountSlotsWithin(runMs) {
  let count = 0;
  for (let at = (accountStride - 1) * slotMs; at < runMs; at += accountStride * slotMs) count += 1;
  return count;
}

export function assertRunningBudget(env = process.env) {
  const step = Number(env.PRODUCTION_STEP_MINUTES);
  const job = Number(env.PRODUCTION_JOB_MINUTES);
  const install = Number(env.PRODUCTION_INSTALL_MINUTES);
  const scrub = Number(env.PRODUCTION_SCRUB_MINUTES);
  const needStep = productionStepMinutes();
  const needJob = installMinutes + needStep + scrubMinutes;
  const runMs = productionInboxRunMs();
  if (accountSlotsWithin(runMs) < productionAccountCreates) {
    throw new Error(`inbox window holds ${accountSlotsWithin(runMs)} account slots for ${productionAccountCreates} creates`);
  }
  if (install !== installMinutes || scrub !== scrubMinutes || step < needStep || job < install + step + scrub) {
    throw new Error(`running workflow budget step=${env.PRODUCTION_STEP_MINUTES} job=${env.PRODUCTION_JOB_MINUTES} install=${env.PRODUCTION_INSTALL_MINUTES} scrub=${env.PRODUCTION_SCRUB_MINUTES}; need step ${needStep}, scrub ${scrubMinutes}, install ${installMinutes}, job at least ${needJob}`);
  }
  return { runMs, suiteMs: productionSuiteMs(), stepMinutes: needStep, jobMinutes: needJob };
}
