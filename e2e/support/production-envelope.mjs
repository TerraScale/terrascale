import { mkdirSync, readFileSync, readdirSync, renameSync, unlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

export const slotMs = 7_000;
export const accountStride = 10;
export const requestMs = 15_000;
export const retryMs = 120_000;
export const mailPollMs = 120_000;
export const envelopeMarker = '[production-envelope]';

const accountCreates = 50;
const mailPolls = 60;
const accountAndTokenMs = (accountStride + 2) * slotMs + 2 * requestMs;
const serialSignupMs = 60_000;
const retryReserveMs = 5 * retryMs;
const launchMs = 120_000;
const sdkAndFinishMs = 30 * 60_000;
const durations = {
  preflight: 180_000,
  suite: launchMs + accountCreates * (accountAndTokenMs + serialSignupMs) + mailPolls * mailPollMs + retryReserveMs + sdkAndFinishMs,
  cleanup: 7 * 60_000,
};

export function envelopeReceipt(state, phase) {
  return join(state, `inbox-infrastructure-${phase}`);
}

export function envelopeError(reason, state, phase = process.env.E2E_PRODUCTION_PHASE ?? 'preflight') {
  const message = `${envelopeMarker} ${reason}`;
  if (state && Object.hasOwn(durations, phase)) {
    try {
      writeFileSync(envelopeReceipt(state, phase), `${message}\n`, { mode: 0o600 });
    } catch {
      return new Error(`${message} (could not write failure receipt)`);
    }
  }
  return new Error(message);
}

function duration(phase) {
  if (!Object.hasOwn(durations, phase)) throw envelopeError('invalid phase');
  return durations[phase];
}

export function beginEnvelope(state, phase) {
  const budget = duration(phase);
  const started = Date.now();
  try {
    if (phase === 'preflight') {
      for (const [name, cutoff] of [['inbox-requests', Math.floor(started / slotMs)], ['inbox-cooldowns', started]]) {
        const directory = join(state, name);
        mkdirSync(directory, { recursive: true, mode: 0o700 });
        for (const entry of readdirSync(directory)) {
          if (!/^\d+$/.test(entry) || !Number.isSafeInteger(Number(entry))) throw envelopeError('invalid scheduler state', state, phase);
          if (Number(entry) < cutoff) unlinkSync(join(directory, entry));
        }
      }
    }
    const file = join(state, 'inbox-envelope.json');
    writeFileSync(`${file}.tmp`, JSON.stringify({ phase, started, deadline: started + budget }), { mode: 0o600 });
    renameSync(`${file}.tmp`, file);
  } catch (error) {
    if (error instanceof Error && error.message.startsWith(envelopeMarker)) throw error;
    throw envelopeError('could not initialize deadline', state, phase);
  }
  return budget;
}

export function envelopeDeadline(state, phase) {
  let value;
  try {
    value = JSON.parse(readFileSync(join(state, 'inbox-envelope.json'), 'utf8'));
  } catch {
    throw envelopeError('missing or invalid deadline', state);
  }
  if (!value || typeof value.phase !== 'string' || !Object.hasOwn(durations, value.phase) || (phase !== undefined && value.phase !== phase)
    || !Number.isSafeInteger(value.started) || !Number.isSafeInteger(value.deadline) || value.started > Date.now()
    || value.deadline - value.started !== duration(value.phase)) throw envelopeError('invalid deadline', state);
  if (value.deadline <= Date.now()) throw envelopeError('deadline elapsed', state);
  return value.deadline;
}
