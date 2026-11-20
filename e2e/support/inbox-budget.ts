import { readFileSync } from 'node:fs';
import { join } from 'node:path';

export const slotMs = 7_000;
export const accountStride = 10;
export const productionAccountCreates = 40;
export const cooldownMs = 60_000;
export const mailPollMs = 120_000;
export const teardownMs = 5 * 60_000;

export function productionInboxRunMs(accounts = productionAccountCreates): number {
  const alignment = (accountStride - 1) * slotMs;
  const span = (accounts - 1) * accountStride * slotMs;
  const tail = 3 * slotMs + mailPollMs + cooldownMs;
  return alignment + span + tail;
}

export function productionSuiteMs(accounts = productionAccountCreates): number {
  return productionInboxRunMs(accounts) + teardownMs;
}

export function productionStepMinutes(accounts = productionAccountCreates): number {
  return Math.ceil(productionSuiteMs(accounts) / 60_000) + 2;
}

export function productionJobMinutes(accounts = productionAccountCreates): number {
  return productionStepMinutes(accounts) + 20;
}

export function assertProductionSchedule(root: string): { runMs: number; suiteMs: number; stepMinutes: number; jobMinutes: number } {
  const stepMinutes = productionStepMinutes();
  const jobMinutes = productionJobMinutes();
  const workflow = readFileSync(join(root, '.github/workflows/production-journey.yml'), 'utf8');
  const job = / {2}journey:[\s\S]*?\n {4}timeout-minutes: (\d+)/.exec(workflow);
  const step = /name: Journey against production\n {8}timeout-minutes: (\d+)/.exec(workflow);
  if (job?.[1] !== String(jobMinutes) || step?.[1] !== String(stepMinutes)) {
    throw new Error(`production journey timeouts are ${job?.[1] ?? 'missing'}/${step?.[1] ?? 'missing'}; the ${productionAccountCreates} account creates need job ${jobMinutes} and step ${stepMinutes}`);
  }
  return { runMs: productionInboxRunMs(), suiteMs: productionSuiteMs(), stepMinutes, jobMinutes };
}
