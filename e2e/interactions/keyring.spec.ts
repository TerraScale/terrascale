import { spawn, type ChildProcess } from 'node:child_process';
import { createCipheriv, createDecipheriv, randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { closeSync, copyFileSync, existsSync, mkdirSync, mkdtempSync, openSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
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

  async function start(active: string, keys: Record<string, string>, binary = candidate, operation?: string): Promise<void> {
    writeFileSync(ringFile, JSON.stringify({ active, keys }), { mode: 0o600 });
    log = join(directory, `node-${++run}.log`);
    const output = openSync(log, 'w', 0o600);
    const environment = Object.fromEntries(Object.entries(process.env).filter(([name]) =>
      !name.startsWith('TERRABASE_') && !name.startsWith('TS_') && !name.startsWith('AWS_') && name !== 'BUCKET_NAME'));
    child = spawn('dotnet', operation ? ['exec', '--depsfile', join(process.env.E2E_BUILD!, 'terrabase/terrabase.deps.json'),
      '--runtimeconfig', join(process.env.E2E_BUILD!, 'terrabase/terrabase.runtimeconfig.json'), process.env.E2E_KEYRING_DRIVER!, operation] : [binary], {
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

  async function rows(operation: 'seed-rows' | 'rows', active: 'k0' | 'k1'): Promise<void> {
    expect(process.env.E2E_KEYRING_DRIVER, 'startup row driver is required').toBeTruthy();
    await start(active, active === 'k0' ? { k0: k0.toString('base64') } : { k0: k0.toString('base64'), k1: k1.toString('base64') }, candidate, operation);
    await expect.poll(() => child?.exitCode).toBe(0);
    expect(readFileSync(log, 'utf8')).toContain('keyring-driver:startup rows=42 databases=' + files(data).length);
    await stop();
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

  async function rotated(expected: number): Promise<void> {
    await ready();
    await expect.poll(async () => {
      const metrics = await (await fetch('http://127.0.0.1:18383/metrics')).text();
      return metrics.match(/terrabase_keys_local_wrapped(?:_\w+)?\{[^\n]*key_id="k0"[^\n]*\} (\d+)/)?.[1];
    }).toBe('0');
    for (const path of files(data)) expect(readFileSync(path, 'ascii').startsWith('TERRABASE-DB-KEY-2\nv2.k1.')).toBe(true);
    expect(files(data)).toHaveLength(expected);
    const metrics = await (await fetch('http://127.0.0.1:18383/metrics')).text();
    expect(metrics).toMatch(new RegExp(`terrabase_keys_local_wrapped(?:_\\w+)?\\{[^\\n]*key_id="k1"[^\\n]*\\} ${expected}(?:\\n|$)`));
  }

  try {
    await start('k1', { k1: k1.toString('base64') });
    await expect.poll(() => child?.exitCode).toBe(2);
    expect(readFileSync(log, 'utf8')).toContain("single master key must use the original id 'k0'");
    await stop();
    await start('k0', { k0: k0.toString('base64') });
    await ready();
    await stop();
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
      await start('k0', { k0: k0.toString('base64') });
      await ready();
      await stop();
    }
    const catalogKey = join(data, '_system.db.key');
    expect(existsSync(catalogKey)).toBe(true);
    const paths = [catalogKey, ...files(data).filter(path => path !== catalogKey)];
    expect(paths.length).toBeGreaterThanOrEqual(3);
    await rows('seed-rows', 'k0');
    const originalBytes = paths.map(path => readFileSync(path));
    await start('k0', { k0: k0.toString('base64') });
    await ready();
    const initial = await (await fetch('http://127.0.0.1:18383/metrics')).text();
    expect(initial).toMatch(new RegExp('terrabase_keys_local_wrapped[^\\n]*key_id="k0"[^\\n]*} ' + paths.length + '(?:\\n|$)'));
    await stop();
    for (const [index, path] of paths.entries()) expect(readFileSync(path).equals(originalBytes[index])).toBe(true);
    const organization = '01J00000000000000000000011';
    const project = '01J00000000000000000000012';
    const database = '01J00000000000000000000013';
    const healthy = '01J00000000000000000000014';
    const parent = join(data, organization, project);
    mkdirSync(parent, { recursive: true });
    const tenant = join(parent, database + '.db');
    for (const id of [database, healthy]) {
      const source = paths[1].slice(0, -4);
      const sourceName = source.slice(source.lastIndexOf('/') + 1);
      for (const name of readdirSync(join(source, '..')).filter(name => name === sourceName || name.startsWith(sourceName + '-') || name === sourceName + '.key')) {
        copyFileSync(join(source, '..', name), join(parent, id + '.db' + name.slice(sourceName.length)));
      }
    }
    function wrap(path: string, id: 'k0' | 'k1'): void {
      const key = dataKey(path);
      const nonce = randomBytes(12);
      const cipher = createCipheriv('aes-256-gcm', id === 'k0' ? k0 : k1, nonce);
      if (id !== 'k0') cipher.setAAD(Buffer.from('v2.' + id));
      const body = Buffer.concat([nonce, cipher.update(key), cipher.final(), cipher.getAuthTag()]).toString('base64');
      writeFileSync(path, id === 'k0' ? 'TERRABASE-DB-KEY-1\n' + body + '\n' : 'TERRABASE-DB-KEY-2\nv2.' + id + '.' + body + '\n', { mode: 0o600 });
      key.fill(0);
    }
    wrap(tenant + '.key', 'k1');
    const tenantBytes = readFileSync(tenant);
    const tenantKey = readFileSync(tenant + '.key');
    await start('k0', { k0: k0.toString('base64') });
    await expect.poll(() => child?.exitCode).toBe(2);
    expect(readFileSync(log, 'utf8')).toContain("unconfigured key id 'k1' is required by 1 local wrappers");
    expect(readFileSync(tenant).equals(tenantBytes)).toBe(true);
    expect(readFileSync(tenant + '.key').equals(tenantKey)).toBe(true);
    await stop();
    await start('k1', { k0: k0.toString('base64'), k1: k1.toString('base64') });
    await rotated(paths.length + 2);
    await stop();
    const requestFile = join(directory, 'refused.json');
    writeFileSync(requestFile, JSON.stringify({ organization, project, database, healthy, mode: 'key-refused' }), { mode: 0o600 });
    wrap(tenant + '.key', 'k0');
    const wrongKeyFile = readFileSync(tenant + '.key');
    await start('k1', { k0: randomBytes(32).toString('base64'), k1: k1.toString('base64') }, candidate, requestFile);
    await expect.poll(() => child?.exitCode).toBe(0);
    expect(readFileSync(log, 'utf8')).toContain('keyring-driver:key-refused counted=1 healthy=42');
    expect(readFileSync(tenant).equals(tenantBytes)).toBe(true);
    expect(readFileSync(tenant + '.key').equals(wrongKeyFile)).toBe(true);
    await stop();
    await start('k1', { k0: k0.toString('base64'), k1: k1.toString('base64') });
    await rotated(paths.length + 2);
    await stop();
    const catalogBytes = readFileSync(catalogKey);
    await start('k1', { k0: k0.toString('base64'), k1: randomBytes(32).toString('base64') });
    await expect.poll(() => child?.exitCode).toBe(2);
    expect(readFileSync(log, 'utf8')).toContain("catalog wrapper is not authentic under key id 'k1'");
    expect(readFileSync(catalogKey).equals(catalogBytes)).toBe(true);
    await stop();
    for (const material of [Buffer.alloc(31).toString('base64'), Buffer.alloc(32, 7).toString('base64')]) {
      await start('k0', { k0: material });
      await expect.poll(() => child?.exitCode).toBe(2);
      expect(readFileSync(log, 'utf8')).toContain('master-key must');
      await stop();
    }
    await start('k0', { k0: k0.toString('base64'), k1: k0.toString('base64') });
    await expect.poll(() => child?.exitCode).toBe(2);
    expect(readFileSync(log, 'utf8')).toContain('duplicates another configured');
    await stop();
    wrap(tenant + '.key', 'k0');
    await start('k1', { k1: k1.toString('base64'), k2: randomBytes(32).toString('base64') });
    await expect.poll(() => child?.exitCode).toBe(2);
    expect(readFileSync(log, 'utf8')).toContain("unconfigured key id 'k0' is required by 1 local wrappers");
    await stop();
    await start('k1', { k0: k0.toString('base64'), k1: k1.toString('base64') });
    await rotated(paths.length + 2);
    await stop();
    await start('k1', { k1: k1.toString('base64'), k2: randomBytes(32).toString('base64') });
    await ready();
    await stop();
    await rows('rows', 'k1');
    console.log('keyring: restored disk missing k1 refused before readiness; configured k1 starts; wrong tenant material counted and healthy row=42; catalog wrong material refuses node; local k0 gauge reaches zero and permits removal; exact length, constant and duplicate material refused');
  } finally {
    await stop();
    k0.fill(0);
    k1.fill(0);
  }
});
