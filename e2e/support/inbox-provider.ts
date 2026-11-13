import { randomBytes } from 'node:crypto';
import { mkdirSync, readdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const slotMs = 7_000;

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
  return `${randomBytes(10).toString('hex')}@${domain}`;
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
      let slot = every * Math.ceil(Math.max(Date.now(), notBefore, cooldown) / (every * slotMs));
      for (;; slot += every) {
        if (slot * slotMs >= deadline) return undefined;
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
      if (Date.now() >= (slot + 1) * slotMs) continue;
      if (readdirSync(cooldowns).some((reset) => Number(reset) > Date.now())) continue;
      return slot;
    }
  }

  async request<T>(method: string, path: string, token?: string, body?: object, deadline = Infinity): Promise<T> {
    const every = method === 'POST' && path === '/accounts' ? 10 : 1;
    let slot = await this.turn(every, Date.now(), deadline);
    if (slot === undefined) throw new Error(`inbox provider mail.tm: ${method} ${path} has no slot before the mail poll deadline`);
    const until = Number.isFinite(deadline) ? deadline : Date.now() + 120_000;
    const headers: Record<string, string> = { accept: 'application/json' };
    if (body) headers['content-type'] = 'application/json';
    if (token) headers.authorization = `Bearer ${token}`;
    for (;;) {
      const remaining = until - Date.now();
      if (remaining <= 0) throw new Error(`inbox provider mail.tm: ${method} ${path} reached the mail poll deadline`);
      const sent = { ...this.context(), pid: process.pid, time: new Date().toISOString(), slot, method, path };
      process.stderr.write(`inbox provider mail.tm: request ${JSON.stringify(sent)}\n`);
      const response = await fetch(`https://api.mail.tm${path}`, {
        method, headers, body: body && JSON.stringify(body), signal: AbortSignal.timeout(Math.min(15_000, remaining)),
      }).catch((error: Error) => {
        throw new Error(`inbox provider mail.tm: ${method} ${path} failed: ${error.cause ?? error.message}`);
      });
      const limits = Object.fromEntries([...response.headers].filter(([name]) => name.startsWith('ratelimit-') || name === 'retry-after'));
      process.stderr.write(`inbox provider mail.tm: response ${JSON.stringify({ ...sent, status: response.status, headers: limits })}\n`);
      if (response.ok) return (await response.json()) as T;
      const detail = (await response.text()).replace(/\s+/g, ' ').slice(0, 500);
      const failure = new Error(`inbox provider mail.tm: ${method} ${path} answered HTTP ${response.status}; headers ${JSON.stringify(limits)}: ${detail}`);
      if (response.status !== 429) throw failure;
      const retryAt = resetAt(response.headers, Date.now());
      writeFileSync(join(this.state, 'inbox-cooldowns', String(Math.ceil(retryAt))), '');
      slot = await this.turn(every, retryAt, until);
      if (slot === undefined) throw failure;
    }
  }
}
