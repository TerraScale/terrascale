import { randomBytes } from 'node:crypto';
import { mkdirSync, readdirSync, unlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { accountStride, envelopeDeadline, envelopeError, requestMs, retryMs, slotMs } from './production-envelope.mjs';

const reservedForAccounts = (slot: number) => slot % accountStride === 0;

function firstSlot(every: number, at: number): number {
  const slot = every * Math.ceil(at / (every * slotMs));
  return every === 1 && reservedForAccounts(slot) ? slot + 1 : slot;
}

function resetAt(headers: Headers, now: number): number {
  const retry = headers.get('retry-after');
  const reset = headers.get('ratelimit-reset');
  const times = [
    retry !== null && /^\d+$/.test(retry) ? now + Number(retry) * 1_000 : Date.parse(retry ?? ''),
    reset !== null && /^\d+$/.test(reset)
      ? Number(reset) >= 1_000_000_000 ? Number(reset) * 1_000 : now + Number(reset) * 1_000
      : Date.parse(reset ?? ''),
  ].filter(Number.isFinite);
  if (times.length) return Math.max(now, ...times);
  const windows = [...(headers.get('ratelimit-policy') ?? '').matchAll(/\bw\s*=\s*(\d+)/g)].map((match) => Number(match[1]) * 1_000);
  return now + Math.max(60_000, ...windows);
}

export function inboxAddress(domain: string): string {
  const bytes = randomBytes(10);
  return `${'abcdef'[bytes[0] % 6]}${bytes.toString('hex').slice(1)}@${domain}`;
}

export class InboxProvider {
  private readonly state: string;
  private readonly onWait: (wait: number) => void;
  private readonly context: () => object;

  constructor(state: string, onWait: (wait: number) => void = () => {}, context: () => object = () => ({})) {
    this.state = state;
    this.onWait = onWait;
    this.context = context;
  }

  private async turn(every: number, notBefore: number, deadline: number): Promise<number | undefined> {
    const slots = join(this.state, 'inbox-requests');
    const cooldowns = join(this.state, 'inbox-cooldowns');
    mkdirSync(slots, { recursive: true });
    mkdirSync(cooldowns, { recursive: true });
    for (;;) {
      const cooldown = Math.max(0, ...readdirSync(cooldowns).map(Number));
      let slot = firstSlot(every, Math.max(Date.now(), notBefore, cooldown));
      for (;; slot += every) {
        if (slot * slotMs >= deadline) return undefined;
        if (every === 1 && reservedForAccounts(slot)) continue;
        try {
          writeFileSync(join(slots, String(slot)), '', { flag: 'wx' });
          break;
        } catch (error) {
          if ((error as NodeJS.ErrnoException).code !== 'EEXIST') throw error;
        }
      }
      const wait = Math.max(0, slot * slotMs - Date.now());
      this.onWait(wait);
      await new Promise((resolve) => setTimeout(resolve, wait));
      const woke = Date.now();
      if (woke < deadline && woke < (slot + 1) * slotMs && !readdirSync(cooldowns).some((reset) => Number(reset) > woke)) return slot;
      unlinkSync(join(slots, String(slot)));
      if (woke >= deadline) return undefined;
    }
  }

  async request<T>(method: string, path: string, token?: string, body?: object, deadline = Infinity): Promise<T> {
    const every = method === 'POST' && path === '/accounts' ? accountStride : 1;
    const run = envelopeDeadline(this.state, process.env.E2E_PRODUCTION_PHASE);
    if (Number.isFinite(deadline) && deadline > run) throw envelopeError('mail poll does not fit before the deadline', this.state);
    let slot = await this.turn(every, Date.now(), Math.min(deadline, run));
    if (slot === undefined) {
      if (deadline >= run) throw envelopeError('no provider slot before the deadline', this.state);
      throw new Error(`inbox provider mail.tm: ${method} ${path} has no slot before the mail poll deadline`);
    }
    const until = Math.min(run, Number.isFinite(deadline) ? deadline : Date.now() + retryMs);
    const headers: Record<string, string> = { accept: 'application/json' };
    if (body) headers['content-type'] = 'application/json';
    if (token) headers.authorization = `Bearer ${token}`;
    for (;;) {
      const remaining = until - Date.now();
      if (remaining <= 0 && until === run) throw envelopeError('provider request reached the deadline', this.state);
      if (remaining <= 0) throw new Error(`inbox provider mail.tm: ${method} ${path} reached the mail poll deadline`);
      const sent = { ...this.context(), pid: process.pid, time: new Date().toISOString(), slot, method, path };
      process.stderr.write(`inbox provider mail.tm: request ${JSON.stringify(sent)}\n`);
      const response = await fetch(`https://api.mail.tm${path}`, {
        method, headers, body: body && JSON.stringify(body), signal: AbortSignal.timeout(Math.min(requestMs, remaining)),
      }).catch((error: Error) => {
        if (until === run && (Date.now() >= run || (remaining <= requestMs && error.name === 'TimeoutError'))) {
          throw envelopeError('provider request reached the deadline', this.state);
        }
        throw new Error(`inbox provider mail.tm: ${method} ${path} failed: ${error.cause ?? error.message}`);
      });
      const limits = Object.fromEntries([...response.headers].filter(([name]) => name.startsWith('ratelimit-') || name === 'retry-after'));
      process.stderr.write(`inbox provider mail.tm: response ${JSON.stringify({ ...sent, status: response.status, headers: limits })}\n`);
      const read = <V>(content: Promise<V>): Promise<V> => content.catch((error: Error) => {
        if (until === run && (Date.now() >= run || (remaining <= requestMs && error.name === 'AbortError'))) {
          throw envelopeError('provider response reached the deadline', this.state);
        }
        throw new Error(`inbox provider mail.tm: ${method} ${path} answered HTTP ${response.status} and its body failed: ${error.cause ?? error.message}`);
      });
      if (response.status === 429) {
        const retryAt = Math.ceil(resetAt(response.headers, Date.now()));
        if (!Number.isSafeInteger(retryAt)) throw envelopeError('invalid provider cooldown', this.state);
        try {
          writeFileSync(join(this.state, 'inbox-cooldowns', String(retryAt)), '');
        } catch {
          throw envelopeError('could not record provider cooldown', this.state);
        }
        const next = firstSlot(every, retryAt) * slotMs;
        if (next >= run) {
          throw envelopeError('provider retry does not fit before the deadline', this.state);
        }
        const detail = (await read(response.text())).replace(/\s+/g, ' ').slice(0, 500);
        slot = await this.turn(every, retryAt, until);
        if (slot !== undefined) continue;
        if (until === run) throw envelopeError('no provider retry slot before the deadline', this.state);
        throw new Error(`inbox provider mail.tm: ${method} ${path} answered HTTP 429; headers ${JSON.stringify(limits)}: ${detail}`);
      }
      if (response.ok) {
        const value = await read(response.json());
        if (Date.now() >= run) throw envelopeError('provider response reached the deadline', this.state);
        return value as T;
      }
      const detail = (await read(response.text())).replace(/\s+/g, ' ').slice(0, 500);
      throw new Error(`inbox provider mail.tm: ${method} ${path} answered HTTP ${response.status}; headers ${JSON.stringify(limits)}: ${detail}`);
    }
  }
}
