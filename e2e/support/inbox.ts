import { AsyncLocalStorage } from 'node:async_hooks';
import { randomBytes } from 'node:crypto';
import { expect, test } from '@playwright/test';
import { cloudflareSink, mailpit, mailWait, production, stateDir } from './stack';
import { saveSecret } from './state';
import { InboxProvider, inboxAddress } from './inbox-provider';

export type Mail = { from: string; fromName?: string; subject: string; text: string };

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
    const message = (await response.json()) as { From: { Address: string; Name: string }; Subject: string; Text: string };
    return { from: message.From.Address, fromName: message.From.Name, subject: message.Subject, text: message.Text };
  },
};

const opened = new Map<string, string>();
const budget = new AsyncLocalStorage<{ timeout: number }>();
const provider = new InboxProvider(stateDir, (wait) => {
  const scope = budget.getStore();
  const timeout = (scope?.timeout ?? test.info().timeout) + wait;
  if (scope) scope.timeout = timeout;
  test.info().setTimeout(timeout);
}, () => {
  const info = test.info();
  return { worker: info.workerIndex, project: info.project.name, spec: info.file, test: info.title };
});

export async function withInboxBudget<T>(step: () => Promise<T>): Promise<T> {
  return budget.run({ timeout: test.info().timeout }, step);
}

async function newest(token: string, seen: number, deadline: number): Promise<Mail | undefined> {
  const messages = await provider.request<{ id: string }[]>('GET', '/messages', token, undefined, deadline);
  if (messages.length <= seen) return undefined;
  const message = await provider.request<{ from: { address: string }; subject: string; text: string }>('GET', `/messages/${messages[0].id}`, token, undefined, deadline);
  return { from: message.from.address, subject: message.subject, text: message.text };
}

const mailTmInbox: Inbox = {
  address: async () => {
    const domain = process.env.E2E_INBOX_DOMAIN;
    if (!domain) throw new Error('inbox provider mail.tm: E2E_INBOX_DOMAIN names no domain; e2e/env/production.sh asks mail.tm for one');
    const address = inboxAddress(domain);
    saveSecret('inbox-address', address);
    const password = randomBytes(24).toString('base64url');
    saveSecret('inbox-password', password);
    await provider.request('POST', '/accounts', undefined, { address, password });
    const { token } = await provider.request<{ token: string }>('POST', '/token', undefined, { address, password });
    saveSecret('inbox-token', token);
    opened.set(address, token);
    return address;
  },
  next: async (address, seen) => {
    const token = opened.get(address);
    if (!token) throw new Error(`inbox provider mail.tm: no inbox for ${address}`);
    const deadline = Date.now() + mailWait;
    let mail: Mail | undefined;
    await expect
      .poll(async () => (mail = await newest(token, seen, deadline)), { message: `mail number ${seen + 1} for the journey address`, timeout: mailWait, intervals: [1_000] })
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
