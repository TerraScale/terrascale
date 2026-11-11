import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { test, type TestInfo } from '@playwright/test';
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

export function saveSecret(name: string, value: string): void {
  mkdirSync(join(stateDir, 'secrets.d'), { recursive: true, mode: 0o700 });
  writeFileSync(join(stateDir, 'secrets.d', name), value, { mode: 0o600 });
}
