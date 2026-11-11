import { randomBytes } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { test, type BrowserContext, type TestInfo } from '@playwright/test';
import { sessionFile, stateDir } from './stack';

export type Journey = {
  runId: string;
  email: string;
  password: string;
  displayName: string;
  organizationId: string;
  organizationName: string;
  projectId: string;
  projectName: string;
  databaseId: string;
  databaseName: string;
  databaseRegion: string;
  apiKeyName: string;
  apiKey: string;
};

const setupOf = (info: TestInfo) => String(info.project.metadata.setup);
const journeyFile = (info: TestInfo) => join(stateDir, `${setupOf(info)}.journey.json`);

export function ownerSession(): string {
  return sessionFile(setupOf(test.info()));
}

export async function saveSession(context: BrowserContext): Promise<void> {
  const session = await context.storageState({ path: ownerSession() });
  for (const cookie of session.cookies) saveSecret(`cookie-${cookie.name}`, cookie.value);
}

export async function saveCookies(context: BrowserContext): Promise<void> {
  for (const cookie of await context.cookies()) saveSecret(`cookie-${cookie.name}`, cookie.value);
}

export function saveJourney(journey: Journey): void {
  mkdirSync(stateDir, { recursive: true, mode: 0o700 });
  writeFileSync(journeyFile(test.info()), JSON.stringify(journey), { mode: 0o600 });
}

export function journey(): Journey {
  return JSON.parse(readFileSync(journeyFile(test.info()), 'utf8')) as Journey;
}

export function setupFinished(info: TestInfo): boolean {
  return existsSync(journeyFile(info));
}

const registered = new Set<string>();

export function mask(value: string): void {
  if (process.env.GITHUB_ACTIONS) process.stdout.write(`::add-mask::${value}\n`);
}

export function saveSecret(name: string, value: string): void {
  if (registered.has(value)) return;
  registered.add(value);
  mkdirSync(join(stateDir, 'secrets.d'), { recursive: true, mode: 0o700 });
  writeFileSync(join(stateDir, 'secrets.d', `${name}-${randomBytes(6).toString('hex')}`), value, { mode: 0o600 });
  mask(value);
}
