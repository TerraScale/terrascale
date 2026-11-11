import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect, test } from '@playwright/test';
import { e2eRoot, stateDir } from './stack';
import { journey, mask } from './state';

export const sdks = ['dotnet', 'go', 'python', 'js', 'rust'] as const;
export type Sdk = (typeof sdks)[number];

type Operation = { id: string; group: 'a' | 'b'; title: string; needs?: string };
type Line = { id: string; ok: boolean; ms: number; error?: string };

const freePlan = { requestsPerSecond: 50, burst: 100 };
const headroom = 20;
const requestsPerRunner: Record<Operation['group'], number> = { a: 37, b: 7 };

let budget: { group: Operation['group']; tokens: number; at: number } | undefined;

function tokensAt(time: number): number {
  return budget ? Math.min(freePlan.burst, budget.tokens + ((time - budget.at) / 1000) * freePlan.requestsPerSecond) : 0;
}

async function paced<T>(group: Operation['group'], run: () => Promise<T>): Promise<T> {
  if (budget?.group !== group) budget = { group, tokens: 0, at: Date.now() };
  const requests = requestsPerRunner[group];
  const wait = Math.max(0, ((requests + headroom - tokensAt(Date.now())) / freePlan.requestsPerSecond) * 1000);
  test.info().annotations.push({ type: 'paced', description: `waited ${Math.ceil(wait)} ms for ${requests + headroom} tokens` });
  await new Promise((resolve) => setTimeout(resolve, wait));
  const result = await run();
  const end = Date.now();
  budget = { group, tokens: tokensAt(end) - requests, at: end };
  return result;
}

const operationsFile = join(e2eRoot, 'sdk', 'operations.json');
const operations = (JSON.parse(readFileSync(operationsFile, 'utf8')) as { operations: Operation[] }).operations;
const built = process.env.E2E_SDK_BUILD ?? join(stateDir, 'sdk');
const secretsDir = join(stateDir, 'secrets.d');

const commands: Record<Sdk, [string, string[]]> = {
  dotnet: ['dotnet', [join(built, 'dotnet', 'Runner.dll')]],
  go: [join(built, 'go', 'runner'), []],
  python: [join(built, 'python', 'bin', 'python'), [join(e2eRoot, 'sdk', 'python', 'runner.py')]],
  js: ['node', [join(e2eRoot, 'sdk', 'js', 'runner.mjs')]],
  rust: [join(built, 'rust', 'runner'), []],
};

function execute(sdk: Sdk, env: NodeJS.ProcessEnv): Promise<{ code: number | null; stdout: string; stderr: string }> {
  const [command, args] = commands[sdk];
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { env: { ...process.env, ...env }, stdio: ['ignore', 'pipe', 'pipe'] });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (chunk: Buffer) => (stdout += chunk.toString()));
    child.stderr.on('data', (chunk: Buffer) => (stderr += chunk.toString()));
    child.on('error', reject);
    child.on('close', (code) => resolve({ code, stdout, stderr }));
  });
}

export async function runOperations(sdk: Sdk, group: 'a' | 'b'): Promise<void> {
  const current = journey();
  mkdirSync(secretsDir, { recursive: true, mode: 0o700 });
  const planned = operations.filter((op) => op.group === group);
  for (const op of planned.filter((candidate) => candidate.needs)) {
    test.info().annotations.push({ type: 'not run', description: `${op.id} (${op.title}): ${op.needs}` });
  }
  const result = await paced(group, () =>
    execute(sdk, {
      TERRASCALE_API_KEY: current.apiKey,
      TERRASCALE_PROJECT_ID: current.projectId,
      TERRASCALE_DATABASE_NAME: current.databaseName,
      E2E_GROUP: group,
      E2E_RUN_ID: `${current.runId}${Date.now().toString(36)}`,
      E2E_OPERATIONS: operationsFile,
      E2E_SECRETS: secretsDir,
    }),
  );
  const scoped = join(secretsDir, `scoped-key-${sdk}`);
  if (existsSync(scoped)) mask(readFileSync(scoped, 'utf8'));
  await test.info().attach(`${sdk} group ${group}`, { body: `${result.stdout}\n${result.stderr}`, contentType: 'text/plain' });
  const lines = result.stdout.split('\n').filter((line) => line.startsWith('{')).map((line) => JSON.parse(line) as Line);
  for (const line of lines) test.info().annotations.push({ type: line.ok ? 'passed' : 'failed', description: `${line.id} ${line.ms} ms${line.error ? `: ${line.error}` : ''}` });
  const expected = planned.filter((op) => !op.needs).map((op) => op.id);
  expect(lines.map((line) => line.id), `${sdk} ran every runnable group ${group} operation once`).toEqual(expected);
  expect(lines.filter((line) => !line.ok).map((line) => `${line.id}: ${line.error}`), `${sdk} group ${group} failures`).toEqual([]);
  expect(result.code, `${sdk} runner exit code (stderr: ${result.stderr.trim().slice(-400)})`).toBe(0);
}
