import { spawn } from 'node:child_process';
import { mkdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect, test } from '@playwright/test';
import { stateDir } from './stack';
import { saveSecret } from './state';

const home = join(stateDir, 'branch-action');
const pullRequest = { number: 42, repository: 314159 };
export const workflowRun = String(Date.now());

type Finished = { code: number | null; stdout: string; stderr: string };

function tools(): string {
  const directory = process.env.E2E_TOOLS;
  if (!directory) throw new Error('E2E_TOOLS names the directory of the pinned tools; env/local.sh sets it');
  return directory;
}

function execute(command: string, args: string[], env: NodeJS.ProcessEnv): Promise<Finished> {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { env, stdio: ['ignore', 'pipe', 'pipe'] });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (chunk: Buffer) => (stdout += chunk.toString()));
    child.stderr.on('data', (chunk: Buffer) => (stderr += chunk.toString()));
    child.on('error', reject);
    child.on('close', (code) => resolve({ code, stdout, stderr }));
  });
}

export function installAction(): void {
  rmSync(home, { recursive: true, force: true });
  mkdirSync(join(home, '.local', 'bin'), { recursive: true, mode: 0o700 });
  symlinkSync(join(tools(), 'ts', '.local', 'bin', 'ts'), join(home, '.local', 'bin', 'ts'));
  writeFileSync(join(home, 'event.json'), JSON.stringify({ number: pullRequest.number, pull_request: { base: { repo: { id: pullRequest.repository } } } }), { mode: 0o600 });
}

export async function branchAction(command: 'create' | 'reset' | 'delete', apiKey: string, database: string, step: string): Promise<Record<string, string>> {
  const output = join(home, `output-${step}`);
  writeFileSync(output, '', { mode: 0o600 });
  const result = await test.step('branch.sh ' + command + ' ' + step, () => execute('bash', [join(tools(), 'branch.sh')], {
    PATH: process.env.PATH,
    HOME: home,
    SSL_CERT_FILE: process.env.SSL_CERT_FILE,
    TERRASCALE_API_KEY: apiKey,
    DATABASE: database,
    BRANCH: 'journey',
    COMMAND: command,
    PROJECT: '',
    TTL: '1h',
    EVENT_ACTION: 'synchronize',
    PR_NUMBER: String(pullRequest.number),
    GITHUB_EVENT_NAME: 'pull_request',
    GITHUB_REPOSITORY_ID: String(pullRequest.repository),
    GITHUB_EVENT_PATH: join(home, 'event.json'),
    GITHUB_OUTPUT: output,
    GITHUB_RUN_ID: workflowRun,
    GITHUB_RUN_ATTEMPT: '1',
  }));
  expect(result.code, `branch.sh ${command} (stderr: ${result.stderr.trim()})`).toBe(0);
  const outputs: Record<string, string> = Object.fromEntries(
    readFileSync(output, 'utf8')
      .split('\n')
      .filter(Boolean)
      .map((line) => [line.slice(0, line.indexOf('=')), line.slice(line.indexOf('=') + 1)]),
  );
  if (outputs['connection-url']) {
    saveSecret('branch-connection-url', outputs['connection-url']);
    saveSecret('branch-scoped-key', decodeURIComponent(new URL(outputs['connection-url']).password));
  }
  return outputs;
}

export function sourceUrl(project: string, database: string): string {
  return `postgresql://any@pg.terrascale.tech:5432/${project}/${database}?sslmode=verify-full&sslrootcert=system`;
}

export async function psql(url: string, password: string, ...statements: string[]): Promise<string[]> {
  const result = await execute('psql', ['-X', '-v', 'ON_ERROR_STOP=1', '-At', ...statements.flatMap((sql) => ['-c', sql]), url], {
    PATH: process.env.PATH,
    HOME: home,
    SSL_CERT_FILE: process.env.SSL_CERT_FILE,
    PGPASSWORD: password,
  });
  expect(result.code, `psql (stderr: ${result.stderr.trim()})`).toBe(0);
  return result.stdout.split('\n').filter(Boolean);
}

export function withoutPassword(connectionUrl: string): { url: string; password: string } {
  const parsed = new URL(connectionUrl);
  const password = decodeURIComponent(parsed.password);
  parsed.password = '';
  return { url: parsed.href, password };
}
