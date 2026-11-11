import { randomBytes } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect, test } from '@playwright/test';
import { cloudflareSink, mailpit, mailWait, production, stateDir } from './stack';
import { saveSecret } from './state';

export type Mail = { from: string; subject: string; text: string };

type Inbox = { address: (name: string) => Promise<string>; next: (address: string, seen: number) => Promise<Mail> };

type Summary = { ID: string };

async function search(address: string): Promise<Summary[]> {
  const response = await fetch(`${mailpit}/api/v1/search?query=${encodeURIComponent(`to:"${address}"`)}`);
  expect(response.status).toBe(200);
  const body = (await response.json()) as { messages: Summary[] };
  return body.messages;
}

const mailpitInbox: Inbox = {
  address: async (name) => `${name}@example.com`,
  next: async (address, seen) => {
    let messages: Summary[] = [];
    await expect
      .poll(async () => (messages = await search(address)).length, { message: `mail number ${seen + 1} for the journey address`, timeout: mailWait, intervals: [100] })
      .toBeGreaterThan(seen);
    const response = await fetch(`${mailpit}/api/v1/message/${messages[0].ID}`);
    expect(response.status).toBe(200);
    const message = (await response.json()) as { From: { Address: string }; Subject: string; Text: string };
    return { from: message.From.Address, subject: message.Subject, text: message.Text };
  },
};

const mailTm = 'https://api.mail.tm';
const requestSpacing = { series: 'inbox-requests', ms: 6_000 };
const accountSpacing = { series: 'inbox-accounts', ms: 60_000 };
const opened = new Map<string, string>();

async function turn(spacing: { series: string; ms: number }): Promise<void> {
  const slots = join(stateDir, spacing.series);
  mkdirSync(slots, { recursive: true });
  for (let slot = Math.ceil(Date.now() / spacing.ms); ; slot += 1) {
    try {
      writeFileSync(join(slots, String(slot)), '', { flag: 'wx' });
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'EEXIST') continue;
      throw error;
    }
    const wait = Math.max(0, slot * spacing.ms - Date.now());
    test.info().setTimeout(test.info().timeout + wait);
    await new Promise((resolve) => setTimeout(resolve, wait));
    return;
  }
}

async function provider<T>(spacing: typeof requestSpacing, method: string, path: string, token?: string, body?: object): Promise<T> {
  await turn(spacing);
  const headers: Record<string, string> = { accept: 'application/json' };
  if (body) headers['content-type'] = 'application/json';
  if (token) headers.authorization = `Bearer ${token}`;
  const response = await fetch(`${mailTm}${path}`, { method, headers, body: body && JSON.stringify(body) }).catch((error: Error) => {
    throw new Error(`inbox provider mail.tm: ${method} ${path} failed: ${error.cause ?? error.message}`);
  });
  if (!response.ok) throw new Error(`inbox provider mail.tm: ${method} ${path} answered HTTP ${response.status}`);
  return (await response.json()) as T;
}

async function newest(token: string, seen: number): Promise<Mail | undefined> {
  const messages = await provider<{ id: string }[]>(requestSpacing, 'GET', '/messages', token);
  if (messages.length <= seen) return undefined;
  const message = await provider<{ from: { address: string }; subject: string; text: string }>(requestSpacing, 'GET', `/messages/${messages[0].id}`, token);
  return { from: message.from.address, subject: message.subject, text: message.text };
}

const mailTmInbox: Inbox = {
  address: async (name) => {
    const domain = process.env.E2E_INBOX_DOMAIN;
    if (!domain) throw new Error('inbox provider mail.tm: E2E_INBOX_DOMAIN names no domain; e2e/env/production.sh asks mail.tm for one');
    const address = `${name}@${domain}`;
    saveSecret('inbox-address', address);
    const password = randomBytes(24).toString('base64url');
    saveSecret('inbox-password', password);
    await provider(accountSpacing, 'POST', '/accounts', undefined, { address, password });
    const { token } = await provider<{ token: string }>(requestSpacing, 'POST', '/token', undefined, { address, password });
    saveSecret('inbox-token', token);
    opened.set(address, token);
    return address;
  },
  next: async (address, seen) => {
    const token = opened.get(address);
    if (!token) throw new Error(`inbox provider mail.tm: no inbox for ${address}`);
    let mail: Mail | undefined;
    await expect
      .poll(async () => (mail = await newest(token, seen)), { message: `mail number ${seen + 1} for the journey address`, timeout: mailWait, intervals: [1_000] })
      .toBeDefined();
    return mail as Mail;
  },
};

const inbox = production ? mailTmInbox : mailpitInbox;

export const newAddress = (name: string): Promise<string> => inbox.address(name);

export const nextMail = (address: string, seen: number): Promise<Mail> => inbox.next(address, seen);

export async function cloudflareSendCount(): Promise<number> {
  const response = await fetch(`${cloudflareSink}/messages`);
  expect(response.status).toBe(200);
  return ((await response.json()) as unknown[]).length;
}
