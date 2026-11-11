import { randomBytes } from 'node:crypto';
import { expect } from '@playwright/test';
import { cloudflareSink, mailpit, mailWait, production } from './stack';
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
const opened = new Map<string, { id: string; token: string }>();

async function provider<T>(method: string, path: string, token?: string, body?: object): Promise<T> {
  const headers: Record<string, string> = { accept: 'application/json' };
  if (body) headers['content-type'] = 'application/json';
  if (token) headers.authorization = `Bearer ${token}`;
  const response = await fetch(`${mailTm}${path}`, { method, headers, body: body && JSON.stringify(body) }).catch((error: Error) => {
    throw new Error(`inbox provider mail.tm: ${method} ${path} failed: ${error.cause ?? error.message}`);
  });
  if (!response.ok) throw new Error(`inbox provider mail.tm: ${method} ${path} answered HTTP ${response.status}`);
  return (response.status === 204 ? undefined : await response.json()) as T;
}

const mailTmInbox: Inbox = {
  address: async (name) => {
    const domains = await provider<{ domain: string; isActive: boolean; isPrivate: boolean }[]>('GET', '/domains');
    const domain = domains.find((candidate) => candidate.isActive && !candidate.isPrivate)?.domain;
    if (!domain) throw new Error('inbox provider mail.tm: GET /domains lists no active public domain');
    const address = `${name}@${domain}`;
    saveSecret('inbox-address', address);
    const password = randomBytes(24).toString('base64url');
    saveSecret('inbox-password', password);
    const { id } = await provider<{ id: string }>('POST', '/accounts', undefined, { address, password });
    const { token } = await provider<{ token: string }>('POST', '/token', undefined, { address, password });
    saveSecret('inbox-token', token);
    opened.set(address, { id, token });
    return address;
  },
  next: async (address, seen) => {
    const inbox = opened.get(address);
    if (!inbox) throw new Error(`inbox provider mail.tm: no open inbox for ${address}`);
    let messages: { id: string }[] = [];
    await expect
      .poll(async () => (messages = await provider<{ id: string }[]>('GET', '/messages', inbox.token)).length, {
        message: `mail number ${seen + 1} for the journey address`,
        timeout: mailWait,
        intervals: [1_000],
      })
      .toBeGreaterThan(seen);
    const message = await provider<{ from: { address: string }; subject: string; text: string }>('GET', `/messages/${messages[0].id}`, inbox.token);
    await provider('DELETE', `/accounts/${inbox.id}`, inbox.token);
    opened.delete(address);
    return { from: message.from.address, subject: message.subject, text: message.text };
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
