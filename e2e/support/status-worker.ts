import { execFile, spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { createWriteStream, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { promisify } from 'node:util';
import { expect } from '@playwright/test';
import { e2eRoot, stateDir } from './stack';

const exec = promisify(execFile);

export type StatusSite = { origin: string; stop: () => Promise<void> };

export function ulid(): string {
  const alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
  const encode = (value: bigint, length: number): string => {
    let text = '';
    for (let i = 0; i < length; i += 1) {
      text = alphabet[Number(value & 31n)] + text;
      value >>= 5n;
    }
    return text;
  };
  let random = 0n;
  for (const byte of randomBytes(10)) random = (random << 8n) | BigInt(byte);
  return encode(BigInt(Date.now()), 10) + encode(random, 16);
}

export async function startStatusSite(sql: string, vars: Record<string, string>, listen = 8791): Promise<StatusSite> {
  const root = join(e2eRoot, '..', 'sites', 'status');
  const persist = join(root, '.wrangler', 'state');
  const log = join(stateDir, `status-site-${listen}.log`);
  mkdirSync(stateDir, { recursive: true, mode: 0o700 });
  rmSync(persist, { recursive: true, force: true });
  rmSync(join(root, '.dev.vars'), { force: true });
  const wrangler = join(root, 'node_modules', '.bin', 'wrangler');
  await exec(wrangler, ['d1', 'execute', 'ts-status', '--local', '--persist-to', persist, '--file', 'schema.sql', '-y'], { cwd: root });
  const catalog = join(stateDir, `status-catalog-${listen}.sql`);
  writeFileSync(catalog, sql, { mode: 0o600 });
  await exec(wrangler, ['d1', 'execute', 'ts-status', '--local', '--persist-to', persist, '--file', catalog, '-y'], { cwd: root });
  if (Object.keys(vars).length > 0) {
    const body = Object.entries(vars).map(([key, value]) => `${key}='${value.replaceAll("'", "'\\''")}'`).join('\n');
    writeFileSync(join(root, '.dev.vars'), `${body}\n`, { mode: 0o600 });
  }
  const output = createWriteStream(log);
  const child = spawn(join(root, 'node_modules', '.bin', 'vp'), ['dev', '--host', '127.0.0.1', '--port', String(listen), '--strictPort'], {
    cwd: root,
    detached: true,
    stdio: ['ignore', 'pipe', 'pipe'],
    env: process.env,
  });
  child.stdout?.pipe(output);
  child.stderr?.pipe(output);
  const origin = `http://127.0.0.1:${listen}`;
  let stopped = false;
  const stop = async (): Promise<void> => {
    if (stopped) return;
    stopped = true;
    if (child.pid !== undefined && child.exitCode === null && child.signalCode === null) {
      try {
        process.kill(-child.pid, 'SIGTERM');
      } catch (error) {
        if ((error as NodeJS.ErrnoException).code !== 'ESRCH') throw error;
      }
      await once(child, 'exit');
    }
    output.end();
    rmSync(join(root, '.dev.vars'), { force: true });
  };
  try {
    await expect.poll(async () => {
      if (child.exitCode !== null) throw new Error(`status site exited ${child.exitCode}\n${readFileSync(log, 'utf8').slice(-2000)}`);
      try {
        return (await fetch(origin)).status;
      } catch (error) {
        const cause = (error as { cause?: NodeJS.ErrnoException }).cause;
        if (cause?.code === 'ECONNREFUSED' || (error as Error).message.includes('fetch failed')) return 0;
        throw error;
      }
    }, { timeout: 60_000 }).toBe(200);
    if (vars.ROUND_NOW !== '1') {
      const armed = await fetch(`${origin}/cdn-cgi/handler/scheduled`);
      if (!armed.ok) throw new Error(`arm ${armed.status} ${await armed.text()}\n${readFileSync(log, 'utf8').slice(-2000)}`);
    }
  } catch (error) {
    await stop();
    throw error;
  }
  return { origin, stop };
}
