import { spawn } from 'node:child_process';
import { mkdirSync, readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect, test } from '@playwright/test';
import type { PaidJourney } from './paid';
import { e2eRoot, production, stateDir } from './stack';
import { journey, mask, type Journey } from './state';

export const sdks = ['dotnet', 'go', 'python', 'js', 'rust'] as const;
export type Sdk = (typeof sdks)[number];

type Group = 'a' | 'b' | 'c';
type Operation = { id: string; group: Group; title: string; needs?: string };
type Line = { id: string; ok: boolean; ms: number; error?: string };
type Plan = { label: string; requestsPerSecond: number; burst: number };

const hobby: Plan = { label: 'hobby', requestsPerSecond: 50, burst: 100 };
const pro: Plan = { label: 'pro', requestsPerSecond: 500, burst: 1000 };
const plans: Record<Group, Plan> = { a: hobby, b: hobby, c: pro };
const headroom = 20;
const twoRegions = !production && process.env.E2E_REGIONS === '2';
const requestsPerRunner: Record<Group, number> = { a: 37, b: twoRegions ? 110 : 6, c: 7 };
const terrabaseMetrics = 'http://127.0.0.1:9091/metrics';

type Lane = { admitted: number; refused: Record<string, number>; body: string };

let budget: { group: Group; tokens: number; at: number } | undefined;
let lastReading = '';

function laneBucket(organizationId: string): string {
  let hash = 2166136261;
  for (let index = 0; index < organizationId.length; index += 1) hash = Math.imul(hash ^ organizationId.charCodeAt(index), 16777619) >>> 0;
  return String(hash & 63);
}

async function lane(organizationId: string, plan: Plan): Promise<Lane> {
  let body = lastReading;
  await expect
    .poll(async () => (body = await (await fetch(terrabaseMetrics)).text()) !== lastReading, {
      message: 'TerraBase metrics newer than the last reading',
      intervals: [50],
    })
    .toBe(true);
  lastReading = body;
  const series = new RegExp(`^terrabase_qos_(request_duration_seconds_count|rejected_total)\\{(?=[^}]*plan="${plan.label}")(?=[^}]*tenant_bucket="${laneBucket(organizationId)}")[^}]*\\} (\\S+)$`);
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

function tokensAt(time: number, plan: Plan): number {
  return budget ? Math.min(plan.burst, budget.tokens + ((time - budget.at) / 1000) * plan.requestsPerSecond) : 0;
}

async function paced<T>(group: Group, run: () => Promise<T>): Promise<T> {
  if (budget?.group !== group) budget = { group, tokens: 0, at: Date.now() };
  const requests = requestsPerRunner[group];
  const plan = plans[group];
  const wait = Math.max(0, ((requests + headroom - tokensAt(Date.now(), plan)) / plan.requestsPerSecond) * 1000);
  test.info().annotations.push({ type: 'paced', description: `waited ${Math.ceil(wait)} ms for ${requests + headroom} tokens` });
  await new Promise((resolve) => setTimeout(resolve, wait));
  const result = await run();
  const end = Date.now();
  budget = { group, tokens: tokensAt(end, plan) - requests, at: end };
  return result;
}

function runnable(op: Operation): boolean {
  return !op.needs || twoRegions && op.id.startsWith('placement.');
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

export async function runOperations(sdk: Sdk, group: Group, management?: { user: string; readKey: string; writeKey: string }): Promise<void> {
  const current = journey<Journey & Partial<Pick<PaidJourney, 'secondDatabaseName'>>>();
  mkdirSync(secretsDir, { recursive: true, mode: 0o700 });
  const planned = operations.filter((op) => op.group === group);
  for (const op of planned.filter((candidate) => !runnable(candidate))) {
    test.info().annotations.push({ type: 'not run', description: `${op.id} (${op.title}): ${op.needs}` });
  }
  const { before, result } = await paced(group, async () => ({
    before: production ? undefined : await lane(current.organizationId, plans[group]),
    result: await execute(sdk, {
      TERRASCALE_API_KEY: current.apiKey,
      TERRASCALE_PROJECT_ID: current.projectId,
      TERRASCALE_DATABASE_NAME: current.databaseName,
      E2E_DATABASE_ID: current.databaseId,
      ...(management ? { E2E_AUTH_USER: management.user, E2E_AUTH_READ_KEY: management.readKey, E2E_AUTH_WRITE_KEY: management.writeKey } : {}),
      ...(current.secondDatabaseName ? { E2E_OTHER_DATABASE: current.secondDatabaseName } : {}),
      E2E_GROUP: group,
      E2E_RUN_ID: `${current.runId}${Date.now().toString(36)}`,
      E2E_OPERATIONS: operationsFile,
      E2E_SECRETS: secretsDir,
    }),
  }));
  if (before) await lane(current.organizationId, plans[group]);
  const used = before && since(await lane(current.organizationId, plans[group]), before);
  const booked = requestsPerRunner[group];
  if (used) {
    const summary = `${sdk} group ${group}: TerraBase admitted ${used.admitted} of the ${booked} requests booked and refused ${used.refused}${used.reasons ? ` (${used.reasons})` : ''}`;
    process.stdout.write(`${summary}\n`);
    test.info().annotations.push({ type: 'lane', description: summary });
  }
  for (const file of readdirSync(secretsDir).filter((name) => name.startsWith(`scoped-key-${sdk}`) || name === `user-api-key-${sdk}`)) mask(readFileSync(join(secretsDir, file), 'utf8'));
  if (production) process.stdout.write(`${sdk} group ${group} runner output:\n${result.stdout}\n${result.stderr}\n`);
  else await test.info().attach(`${sdk} group ${group}`, { body: `${result.stdout}\n${result.stderr}`, contentType: 'text/plain' });
  if (twoRegions) process.stdout.write(result.stdout.split('\n').filter(line => line.includes('placement:')).join('\n') + '\n');
  const lines = result.stdout.split('\n').filter((line) => line.startsWith('{')).map((line) => JSON.parse(line) as Line);
  for (const line of lines) test.info().annotations.push({ type: line.ok ? 'passed' : 'failed', description: `${line.id} ${line.ms} ms${line.error ? `: ${line.error}` : ''}` });
  if (used) {
    const budgetRule = `the pacing in e2e/support/sdk.ts books ${booked} requests per group ${group} runner (requestsPerRunner)`;
    expect(used.refused, `${sdk} runner: TerraBase refused ${used.refused} of its requests with tenant_busy${used.reasons ? ` (${used.reasons})` : ''}; ${budgetRule}`).toBe(0);
    expect(used.admitted, `${sdk} runner: TerraBase admitted ${used.admitted} of its requests; ${budgetRule}`).toBeLessThanOrEqual(booked);
    expect(used.admitted, `${sdk} runner: TerraBase admitted none of its requests in the journey organization's lane, so support/sdk.ts reads the wrong metric`).toBeGreaterThan(0);
  }
  const expected = planned.filter(runnable).map((op) => op.id);
  expect(lines.map((line) => line.id), `${sdk} ran every runnable group ${group} operation once`).toEqual(expected);
  expect(lines.filter((line) => !line.ok).map((line) => `${line.id}: ${line.error}`), `${sdk} group ${group} failures`).toEqual([]);
  expect(result.code, `${sdk} runner exit code (stderr: ${result.stderr.trim().slice(-400)})`).toBe(0);
}

export async function runSessionFault(sdk: Sdk, mode: string, asynchronous = false): Promise<void> {
  const { sessionProxy } = await import('../sdk/session-proxy');
  const proxy = await sessionProxy(mode);
  const current = journey();
  try {
    const result = await paced('a', () => execute(sdk, {
      TERRASCALE_API_KEY: current.apiKey,
      TERRASCALE_PROJECT_ID: current.projectId,
      TERRASCALE_DATABASE_NAME: current.databaseName,
      E2E_SESSION_PROXY: proxy.origin,
      E2E_SESSION_TABLE: `lost_${sdk}_${Date.now().toString(36)}`,
      E2E_SESSION_FAULT: mode,
      E2E_SESSION_ASYNC: asynchronous ? '1' : '',
    }));
    const proof = proxy.proof();
    console.log(`${sdk} ${mode}${asynchronous ? ' async' : ''}: ${result.stdout.trim()} ${JSON.stringify(proof)}`);
    await test.info().attach(`${sdk}-${mode}${asynchronous ? '-async' : ''}`, {
      body: JSON.stringify({ ...result, ...proof }), contentType: 'application/json',
    });
    expect(result.code, result.stderr).toBe(0);
    const answer = JSON.parse(result.stdout.trim()) as { first: string; next: string; rows: string };
    const expected = mode === 'before' ? ['transaction_rolled_back'] : ['commit_uncertain', 'write_outcome_unknown'];
    if (mode === 'read') {
      expect(['commit_uncertain', 'write_outcome_unknown', 'accepted']).not.toContain(answer.first);
      expect(answer.next).toBe('transaction_rolled_back');
    } else {
      expect(expected, `${sdk} first error: ${answer.first}`).toContain(answer.first);
      expect(answer.next, 'later calls retain the failure').toBe(answer.first);
    }
    expect(answer.rows, 'the lost reply does not imply rollback').toBe(['before', 'read'].includes(mode) ? '0' : mode === 'batch' ? '2' : '1');
    expect(proof.dropped).toBe(true);
    expect(proof.upgrades, 'no silent reconnect').toBe(1);
    if (mode === 'before') {
      expect(proof.requests.filter(type => type === 'commit')).toHaveLength(0);
      expect(proof.requests.slice(0, 2)).toEqual(['begin', 'execute']);
      expect(proof.requests.length).toBeLessThanOrEqual(3);
    } else {
      expect(proof.requests, 'no replay or fresh transaction').toEqual(['autocommit', 'batch'].includes(mode) ? ['execute'] : mode === 'read' ? ['begin', 'execute', 'execute'] : ['begin', 'execute', 'commit']);
    }
  } finally { await proxy.close(); }
}
