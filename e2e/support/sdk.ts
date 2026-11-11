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
const requestsPerRunner: Record<Operation['group'], number> = { a: 37, b: 6 };
const terrabaseMetrics = 'http://127.0.0.1:9091/metrics';

type Lane = { admitted: number; refused: Record<string, number>; body: string };

let budget: { group: Operation['group']; tokens: number; at: number } | undefined;
let lastReading = '';

function laneBucket(organizationId: string): string {
  let hash = 2166136261;
  for (let index = 0; index < organizationId.length; index += 1) hash = Math.imul(hash ^ organizationId.charCodeAt(index), 16777619) >>> 0;
  return String(hash & 63);
}

async function lane(organizationId: string): Promise<Lane> {
  let body = lastReading;
  await expect
    .poll(async () => (body = await (await fetch(terrabaseMetrics)).text()) !== lastReading, {
      message: 'TerraBase metrics newer than the last reading',
      intervals: [50],
    })
    .toBe(true);
  lastReading = body;
  const series = new RegExp(`^terrabase_qos_(request_duration_seconds_count|rejected_total)\\{(?=[^}]*plan="hobby")(?=[^}]*tenant_bucket="${laneBucket(organizationId)}")[^}]*\\} (\\S+)$`);
  const reading: Lane = { admitted: 0, refused: {}, body };
  for (const line of body.split('\n')) {
    const match = series.exec(line);
    if (!match) continue;
    if (match[1] === 'request_duration_seconds_count') reading.admitted += Number(match[2]);
    else {
      const reason = /reason="([a-z_]+)"/.exec(line)?.[1] ?? 'unknown';
      reading.refused[reason] = (reading.refused[reason] ?? 0) + Number(match[2]);
    }
  }
  return reading;
}

function since(after: Lane, before: Lane): { admitted: number; refused: number; reasons: string } {
  const reasons = Object.entries(after.refused)
    .map(([reason, count]) => [reason, count - (before.refused[reason] ?? 0)] as const)
    .filter(([, count]) => count > 0);
  return {
    admitted: after.admitted - before.admitted,
    refused: reasons.reduce((total, [, count]) => total + count, 0),
    reasons: reasons.map(([reason, count]) => `${count} ${reason}`).join(', '),
  };
}

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
  const { before, result } = await paced(group, async () => ({
    before: await lane(current.organizationId),
    result: await execute(sdk, {
      TERRASCALE_API_KEY: current.apiKey,
      TERRASCALE_PROJECT_ID: current.projectId,
      TERRASCALE_DATABASE_NAME: current.databaseName,
      E2E_GROUP: group,
      E2E_RUN_ID: `${current.runId}${Date.now().toString(36)}`,
      E2E_OPERATIONS: operationsFile,
      E2E_SECRETS: secretsDir,
    }),
  }));
  const used = since(await lane(current.organizationId), before);
  const booked = requestsPerRunner[group];
  const summary = `${sdk} group ${group}: TerraBase admitted ${used.admitted} of the ${booked} requests booked and refused ${used.refused}${used.reasons ? ` (${used.reasons})` : ''}`;
  process.stdout.write(`${summary}\n`);
  test.info().annotations.push({ type: 'lane', description: summary });
  const scoped = join(secretsDir, `scoped-key-${sdk}`);
  if (existsSync(scoped)) mask(readFileSync(scoped, 'utf8'));
  await test.info().attach(`${sdk} group ${group}`, { body: `${result.stdout}\n${result.stderr}`, contentType: 'text/plain' });
  const lines = result.stdout.split('\n').filter((line) => line.startsWith('{')).map((line) => JSON.parse(line) as Line);
  for (const line of lines) test.info().annotations.push({ type: line.ok ? 'passed' : 'failed', description: `${line.id} ${line.ms} ms${line.error ? `: ${line.error}` : ''}` });
  const budgetRule = `the pacing in e2e/support/sdk.ts books ${booked} requests per group ${group} runner (requestsPerRunner)`;
  expect(used.refused, `${sdk} runner: TerraBase refused ${used.refused} of its requests with tenant_busy${used.reasons ? ` (${used.reasons})` : ''}; ${budgetRule}`).toBe(0);
  expect(used.admitted, `${sdk} runner: TerraBase admitted ${used.admitted} of its requests; ${budgetRule}`).toBeLessThanOrEqual(booked);
  expect(used.admitted, `${sdk} runner: TerraBase admitted none of its requests in the journey organization's lane, so support/sdk.ts reads the wrong metric`).toBeGreaterThan(0);
  const expected = planned.filter((op) => !op.needs).map((op) => op.id);
  expect(lines.map((line) => line.id), `${sdk} ran every runnable group ${group} operation once`).toEqual(expected);
  expect(lines.filter((line) => !line.ok).map((line) => `${line.id}: ${line.error}`), `${sdk} group ${group} failures`).toEqual([]);
  expect(result.code, `${sdk} runner exit code (stderr: ${result.stderr.trim().slice(-400)})`).toBe(0);
}
