import { spawn, type ChildProcess } from 'node:child_process';
import { createCipheriv, createDecipheriv, createHash, randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { closeSync, existsSync, mkdirSync, mkdtempSync, openSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { test, expect } from '@playwright/test';
import { production, stateDir } from '../support/stack';
import { saveSecret } from '../support/state';

test.skip(production, 'key rotation requires restarting a throwaway node');

test('a node opens k0 sidecars, resumes rotation and proves k0 has no remaining wrappers', async () => {
  mkdirSync(stateDir, { recursive: true });
  const directory = mkdtempSync(join(stateDir, 'keyring-'));
  const data = join(directory, 'data');
  const ringFile = join(directory, 'ring.json');
  const k0 = randomBytes(32);
  const k1 = randomBytes(32);
  const peer = randomBytes(32).toString('hex');
  for (const [name, value] of [['keyring-k0', k0.toString('base64')], ['keyring-k1', k1.toString('base64')], ['keyring-peer', peer]]) saveSecret(name, value);
  const candidate = join(process.env.E2E_BUILD!, 'terrabase', 'terrabase.dll');
  let child: ChildProcess | undefined;
  let log = '';
  let run = 0;
  const files = (path: string): string[] => readdirSync(path, { withFileTypes: true }).flatMap(entry =>
    entry.isDirectory() ? entry.name === '.replicas' ? [] : files(join(path, entry.name)) : entry.name.endsWith('.db.key') ? [join(path, entry.name)] : []);

  async function stop(): Promise<void> {
    if (child && child.exitCode === null && child.signalCode === null) {
      const exited = once(child, 'exit');
      child.kill('SIGTERM');
      await exited;
    }
    child = undefined;
  }

  async function start(active: string, keys: Record<string, string>, binary = candidate): Promise<void> {
    writeFileSync(ringFile, JSON.stringify({ active, keys }), { mode: 0o600 });
    log = join(directory, `node-${++run}.log`);
    const output = openSync(log, 'w', 0o600);
    const environment = Object.fromEntries(Object.entries(process.env).filter(([name]) =>
      !name.startsWith('TERRABASE_') && !name.startsWith('TS_') && !name.startsWith('AWS_') && name !== 'BUCKET_NAME'));
    child = spawn('dotnet', [binary], {
      env: {
        ...environment,
        DOTNET_ROOT: process.env.DOTNET_ROOT,
        DOTNET_PROCESSOR_COUNT: '4',
        DOTNET_ENVIRONMENT: 'Development',
        TERRABASE_MASTER_KEYS_FILE: ringFile,
        TERRABASE_PEER_SECRET: peer,
        TERRABASE_DATA_DIRECTORY: data,
        TERRABASE_MEMORY_BUDGET: '67108864',
        TERRABASE_STORAGE_THREADS: '1',
        TERRABASE_PORT: '18380',
        TERRABASE_INTERNAL_PORT: '18381',
        TERRABASE_POSTGRES_PORT: '18382',
        TERRABASE_METRICS_PORT: '18383',
      },
      stdio: ['ignore', output, output],
    });
    closeSync(output);

  }

  async function ready(): Promise<void> {
    await expect.poll(async () => {
      expect(child?.exitCode, 'the throwaway node remains running').toBeNull();
      try {
        return (await fetch('http://127.0.0.1:18380/readyz')).status;
      } catch {
        return 0;
      }
    }).toBe(200);
  }

  function dataKey(path: string): Buffer {
    const text = readFileSync(path, 'ascii').trim().split('\n')[1];
    const parts = text.split('.');
    const wrapped = Buffer.from(parts.length === 3 ? parts[2] : text, 'base64');
    const decipher = createDecipheriv('aes-256-gcm', parts[1] === 'k1' ? k1 : k0, wrapped.subarray(0, 12));
    if (parts.length === 3) decipher.setAAD(Buffer.from(`v2.${parts[1]}`));
    decipher.setAuthTag(wrapped.subarray(-16));
    return Buffer.concat([decipher.update(wrapped.subarray(12, -16)), decipher.final()]);
  }

  function legacy(path: string): Buffer {
    const key = dataKey(path);
    const nonce = randomBytes(12);
    const cipher = createCipheriv('aes-256-gcm', k0, nonce);
    const body = Buffer.concat([nonce, cipher.update(key), cipher.final(), cipher.getAuthTag()]);
    writeFileSync(path, `TERRABASE-DB-KEY-1\n${body.toString('base64')}\n`, { mode: 0o600 });
    return key;
  }

  async function refused(): Promise<void> {
    await expect.poll(async () => {
      try {
        return (await fetch('http://127.0.0.1:18380/readyz')).status;
      } catch {
        return 0;
      }
    }).not.toBe(200);
  }

  async function rotated(expected: number): Promise<void> {
    await ready();
    await expect.poll(async () => {
      const metrics = await (await fetch('http://127.0.0.1:18383/metrics')).text();
      return metrics.match(/terrabase_keys_wrapped(?:_\w+)?\{[^\n]*key_id="k0"[^\n]*\} (\d+)/)?.[1];
    }).toBe('0');
    for (const path of files(data)) expect(readFileSync(path, 'ascii').startsWith('TERRABASE-DB-KEY-2\nv2.k1.')).toBe(true);
    expect(files(data)).toHaveLength(expected);
    const metrics = await (await fetch('http://127.0.0.1:18383/metrics')).text();
    expect(metrics).toMatch(new RegExp(`terrabase_keys_wrapped(?:_\\w+)?\\{[^\\n]*key_id="k1"[^\\n]*\\} ${expected}(?:\\n|$)`));
  }

  try {
    if (process.env.E2E_KEYRING_BASELINE) {
      const original = process.env.E2E_KEYRING_BASELINE;
      const output = openSync(join(directory, 'origin.log'), 'w', 0o600);
      const environment = Object.fromEntries(Object.entries(process.env).filter(([name]) =>
        !name.startsWith('TERRABASE_') && !name.startsWith('TS_') && !name.startsWith('AWS_') && name !== 'BUCKET_NAME'));
      child = spawn('dotnet', [resolve(original)], {
        env: { ...environment, DOTNET_ENVIRONMENT: 'Development', DOTNET_PROCESSOR_COUNT: '4',
          TERRABASE_MASTER_KEY: k0.toString('base64'), TERRABASE_PEER_SECRET: peer, TERRABASE_DATA_DIRECTORY: data,
          TERRABASE_MEMORY_BUDGET: '67108864', TERRABASE_STORAGE_THREADS: '1',
          TERRABASE_PORT: '18380', TERRABASE_INTERNAL_PORT: '18381', TERRABASE_POSTGRES_PORT: '18382', TERRABASE_METRICS_PORT: '18383' },
        stdio: ['ignore', output, output],
      });
      closeSync(output);
      await ready();
      await stop();
    } else {
      await start('k0', { k0: k0.toString('base64'), k1: k1.toString('base64') });
      await ready();
      await stop();
    }
    const catalogKey = join(data, '_system.db.key');
    expect(existsSync(catalogKey)).toBe(true);
    const paths = [catalogKey, ...files(data).filter(path => path !== catalogKey)];
    expect(paths.length).toBeGreaterThanOrEqual(3);
    const originalBytes = paths.map(path => readFileSync(path));
    await start('k0', { k0: k0.toString('base64') });
    await ready();
    await expect.poll(async () => Number((await (await fetch('http://127.0.0.1:18383/metrics')).text())
      .match(/terrabase_keys_last_complete[^\n]* (\d+)/)?.[1] ?? '0')).toBeGreaterThan(0);
    await stop();
    for (const [index, path] of paths.entries()) expect(readFileSync(path).equals(originalBytes[index])).toBe(true);
    const originalKeys = paths.map(path => createHash('sha256').update(process.env.E2E_KEYRING_BASELINE ? dataKey(path) : legacy(path)).digest('hex'));
    await start('k1', { k0: k0.toString('base64'), k1: k1.toString('base64') });
    await rotated(paths.length);
    await stop();
    expect(createHash('sha256').update(legacy(paths[0])).digest('hex')).toBe(originalKeys[0]);
    await start('k1', { k0: k0.toString('base64'), k1: k1.toString('base64') });
    await rotated(paths.length);
    await stop();
    const replicaDirectory = join(data, '.replicas', 'inventory');
    mkdirSync(replicaDirectory, { recursive: true });
    const orphan = join(replicaDirectory, 'orphan.promote.db.key');
    const malformed = join(replicaDirectory, 'aaa.key');
    const later = join(replicaDirectory, 'zzz.key');
    writeFileSync(orphan, 'interrupted promote', { mode: 0o600 });
    writeFileSync(malformed, 'malformed replica key', { mode: 0o600 });
    legacy(paths[0]);
    const existingWrapped = readFileSync(paths[0], 'ascii').split('\n')[1];
    writeFileSync(later, `TERRABASE-REPLICA-KEY-1\n${existingWrapped}\n`, { mode: 0o600 });
    await start('k1', { k0: k0.toString('base64'), k1: k1.toString('base64') });
    await ready();
    await expect.poll(async () => (await (await fetch('http://127.0.0.1:18383/metrics')).text())
      .match(/terrabase_keys_wrapped[^\n]*status="unreadable"[^\n]*\} (\d+)/)?.[1]).toBe('1');
    expect(readFileSync(later, 'ascii').startsWith('TERRABASE-REPLICA-KEY-2\nv2.k1.')).toBe(true);
    expect(readFileSync(orphan, 'ascii')).toBe('interrupted promote');
    expect(readFileSync(log, 'utf8').split('\n').filter(line =>
      line.includes('"component":"TerraBase.Features.Backup.KeyRewrap"') && line.includes(malformed))).toHaveLength(1);
    await stop();
    rmSync(replicaDirectory, { recursive: true });
    await start('k1', { k1: k1.toString('base64') });
    await ready();
    await stop();
    legacy(paths[0]);
    await start('k1', { k1: k1.toString('base64') });
    await expect.poll(() => existsSync(log) && readFileSync(log, 'utf8').includes("unconfigured key id 'k0'")).toBe(true);
    await refused();
    await stop();
    const record = readFileSync(paths[0], 'ascii');
    writeFileSync(paths[0], record.replace('TERRABASE-DB-KEY-1\n', 'TERRABASE-DB-KEY-2\nv2.k1.'));
    await start('k1', { k0: k0.toString('base64'), k1: k1.toString('base64') });
    await expect.poll(() => existsSync(log) && readFileSync(log, 'utf8').includes("not authentic under key id 'k1'")).toBe(true);
    await refused();
    await stop();
    writeFileSync(paths[0], record.replace('TERRABASE-DB-KEY-1\n', 'TERRABASE-DB-KEY-2\nv2.unknown.'));
    await start('k1', { k0: k0.toString('base64'), k1: k1.toString('base64') });
    await expect.poll(() => existsSync(log) && readFileSync(log, 'utf8').includes("unconfigured key id 'unknown'")).toBe(true);
    await refused();
    await stop();
    writeFileSync(paths[0], record.replace('TERRABASE-DB-KEY-1\n', 'TERRABASE-DB-KEY-2\nv2..'));
    await start('k1', { k0: k0.toString('base64'), k1: k1.toString('base64') });
    await expect.poll(() => existsSync(log) && readFileSync(log, 'utf8').includes('version or key id is malformed')).toBe(true);
    await refused();
    await stop();
    await start('absent', { k0: k0.toString('base64'), k1: k1.toString('base64') });
    await expect.poll(() => child?.exitCode).toBe(2);
    expect(readFileSync(log, 'utf8').includes('configured active key')).toBe(true);
    console.log(`keyring: ${paths.length} existing encrypted databases opened; mixed k0/k1 sidecars resumed; k0=0 k1=${paths.length}; unchanged k0 upgrade; malformed item counted and later replica rotated; promote orphan skipped; k0 retired; missing, wrong and unknown ids refused; missing active key rejected`);
  } finally {
    await stop();
    k0.fill(0);
    k1.fill(0);
  }
});
