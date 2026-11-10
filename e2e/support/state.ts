import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { stateDir } from './stack';

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

const file = join(stateDir, 'journey.json');

export function saveJourney(journey: Journey): void {
  mkdirSync(stateDir, { recursive: true, mode: 0o700 });
  writeFileSync(file, JSON.stringify(journey), { mode: 0o600 });
}

export function journey(): Journey {
  return JSON.parse(readFileSync(file, 'utf8')) as Journey;
}

export function saveSecret(name: string, value: string): void {
  mkdirSync(join(stateDir, 'secrets.d'), { recursive: true, mode: 0o700 });
  writeFileSync(join(stateDir, 'secrets.d', name), value, { mode: 0o600 });
}
