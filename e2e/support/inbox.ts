import { expect } from '@playwright/test';
import { cloudflareSink, mailpit } from './stack';

export type Mail = { from: string; subject: string; text: string };

type Summary = { ID: string };

async function search(address: string): Promise<Summary[]> {
  const response = await fetch(`${mailpit}/api/v1/search?query=${encodeURIComponent(`to:"${address}"`)}`);
  expect(response.status).toBe(200);
  const body = (await response.json()) as { messages: Summary[] };
  return body.messages;
}

export async function nextMail(address: string, seen: number): Promise<Mail> {
  let messages: Summary[] = [];
  await expect
    .poll(async () => (messages = await search(address)).length, { message: `mail number ${seen + 1} for the journey address`, timeout: 30_000, intervals: [100] })
    .toBeGreaterThan(seen);
  const response = await fetch(`${mailpit}/api/v1/message/${messages[0].ID}`);
  expect(response.status).toBe(200);
  const message = (await response.json()) as { From: { Address: string }; Subject: string; Text: string };
  return { from: message.From.Address, subject: message.Subject, text: message.Text };
}

export async function cloudflareSendCount(): Promise<number> {
  const response = await fetch(`${cloudflareSink}/messages`);
  expect(response.status).toBe(200);
  return ((await response.json()) as unknown[]).length;
}
