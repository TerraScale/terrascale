import { spawn, type ChildProcess } from 'node:child_process';
import { createCipheriv, createDecipheriv, randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { chmodSync, closeSync, copyFileSync, existsSync, mkdirSync, mkdtempSync, openSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { test, expect } from '@playwright/test';
import { production, stateDir } from '../support/stack';
import { saveSecret } from '../support/state';

test.skip(production, 'key rotation requires restarting a throwaway node');
test.describe.configure({ mode: 'default' });

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

  async function start(active: string, keys: Record<string, string>, binary = candidate, operation?: string | string[], dataDirectory = data): Promise<void> {
    writeFileSync(ringFile, JSON.stringify({ active, keys }), { mode: 0o600 });
    log = join(directory, `node-${++run}.log`);
    const output = openSync(log, 'w', 0o600);
    const environment = Object.fromEntries(Object.entries(process.env).filter(([name]) =>
      !name.startsWith('TERRABASE_') && !name.startsWith('TS_') && !name.startsWith('AWS_') && name !== 'BUCKET_NAME'));
    const executable = process.getuid?.() === 0 ? 'setpriv' : 'dotnet';
    const confinement = executable === 'setpriv' ? ['--bounding-set=-dac_override,-dac_read_search', 'dotnet'] : [];
    child = spawn(executable, [...confinement, ...(operation ? ['exec', '--depsfile', join(process.env.E2E_BUILD!, 'terrabase/terrabase.deps.json'),
      '--runtimeconfig', join(process.env.E2E_BUILD!, 'terrabase/terrabase.runtimeconfig.json'), process.env.E2E_KEYRING_DRIVER!, ...(Array.isArray(operation) ? operation : [operation])] : [binary])], {
      env: {
        ...environment,
        DOTNET_ROOT: process.env.DOTNET_ROOT,
        DOTNET_PROCESSOR_COUNT: '4',
        DOTNET_ENVIRONMENT: 'Development',
        TERRABASE_MASTER_KEYS_FILE: ringFile,
        TERRABASE_PEER_SECRET: peer,
        TERRABASE_DATA_DIRECTORY: dataDirectory,
        TERRABASE_MEMORY_BUDGET: '67108864',
        TERRABASE_STORAGE_THREADS: '1',
        TERRABASE_PORT: '18380',
        TERRABASE_INTERNAL_PORT: '18381',
        TERRABASE_POSTGRES_PORT: '18382',
        TERRABASE_METRICS_PORT: '18383',
      },
      stdio: [operation ? 'pipe' : 'ignore', output, output],
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

  async function rotated(expected: number, credentials = 0): Promise<void> {
    await ready();
    await expect.poll(async () => {
      const metrics = await (await fetch('http://127.0.0.1:18383/metrics')).text();
      return metrics.match(/terrabase_keys_local_wrapped(?:_\w+)?\{[^\n]*key_id="k0"[^\n]*\} (\d+)/)?.[1];
    }).toBe('0');
    for (const path of files(data)) expect(readFileSync(path, 'ascii').startsWith('TERRABASE-DB-KEY-2\nv2.k1.')).toBe(true);
    expect(files(data)).toHaveLength(expected);
    const metrics = await (await fetch('http://127.0.0.1:18383/metrics')).text();
    expect(metrics).toMatch(new RegExp(`terrabase_keys_local_wrapped(?:_\\w+)?\\{[^\\n]*key_id="k1"[^\\n]*\\} ${expected + credentials}(?:\\n|$)`));
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
    const allFiles = (path: string): string[] => readdirSync(path, { withFileTypes: true }).flatMap(entry =>
      entry.isDirectory() ? allFiles(join(path, entry.name)) : [join(path, entry.name)]);
    const baselineRetained = allFiles(data).filter(path => path.includes('.creating'));
    const baselineBytes = baselineRetained.reduce((sum, path) => sum + readFileSync(path).length, 0);
    const partialCatalog = catalogKey + '.rewrapping.interrupted';
    const partialTenant = tenant + '.key.rewrapping.interrupted';
    writeFileSync(partialCatalog, 'TERRABASE-DB-KEY-2\nv2.k1.partial');
    writeFileSync(partialTenant, 'TERRABASE-DB-KEY-2\nv2.k1.partial');
    const detachedProof = join(parent, '01J00000000000000000000015.promote.db');
    const retainedProof = new Map([
      [detachedProof, readFileSync(tenant)],
      [detachedProof + '.key', readFileSync(tenant + '.key')],
      [detachedProof + '-wal', Buffer.from('retained log evidence')],
    ]);
    for (const [path, content] of retainedProof) writeFileSync(path, content, { mode: 0o600 });
    const retainedBytes = [...retainedProof.values()].reduce((sum, content) => sum + content.length, 0);
    writeFileSync(requestFile, JSON.stringify({ organization, project, database, mode: 'temporary', files: baselineRetained.length + retainedProof.size + 2,
      bytes: baselineBytes + retainedBytes + readFileSync(partialCatalog).length + readFileSync(partialTenant).length }), { mode: 0o600 });
    await start('k1', { k0: k0.toString('base64'), k1: k1.toString('base64') }, candidate, requestFile);
    await expect.poll(() => child?.exitCode).toBe(0);
    expect(readFileSync(log, 'utf8')).toContain('keyring-driver:partial-temporaries catalog-started tenant=42 unknown=k1 retained=' + (baselineRetained.length + retainedProof.size + 2));
    await stop();
    expect(existsSync(partialCatalog)).toBe(true);
    expect(existsSync(partialTenant)).toBe(true);
    rmSync(partialCatalog);
    rmSync(partialTenant);
    for (const [path, content] of retainedProof) {
      expect(readFileSync(path).equals(content)).toBe(true);
      rmSync(path);
    }
    const unauthentic = tenant + '.key.rewrapping.unauthentic';
    const wrapper = readFileSync(tenant + '.key', 'ascii').trim().split('\n')[1].split('.');
    const badTag = Buffer.from(wrapper[2], 'base64');
    badTag[badTag.length - 1] ^= 1;
    writeFileSync(unauthentic, 'TERRABASE-DB-KEY-2\nv2.k1.' + badTag.toString('base64') + '\n');
    writeFileSync(requestFile, JSON.stringify({ organization, project, database, mode: 'temporary',
      files: baselineRetained.length + 1, bytes: baselineBytes + readFileSync(unauthentic).length }), { mode: 0o600 });
    await start('k1', { k0: k0.toString('base64'), k1: k1.toString('base64') }, candidate, requestFile);
    await expect.poll(() => child?.exitCode).toBe(0);
    expect(readFileSync(log, 'utf8')).toContain('keyring-driver:partial-temporaries catalog-started tenant=42 unknown=k1 retained=' + (baselineRetained.length + 1));
    await stop();
    expect(existsSync(unauthentic)).toBe(true);
    rmSync(unauthentic);
    const intent = tenant.replace('.db', '.promote.db') + '.importing';
    writeFileSync(intent, '{}');
    const intentBytes = readFileSync(intent);
    chmodSync(intent, 0);
    try {
      writeFileSync(requestFile, JSON.stringify({ organization, project, database, healthy, mode: 'key-refused', unreadable: intent,
        files: baselineRetained.length + 1, bytes: baselineBytes + intentBytes.length }), { mode: 0o600 });
      await start('k1', { k0: k0.toString('base64'), k1: k1.toString('base64') }, candidate, requestFile);
      await expect.poll(() => child?.exitCode).toBe(0);
      expect(readFileSync(log, 'utf8')).toContain('keyring-driver:key-refused counted=1 healthy=42');
      expect(readFileSync(log, 'utf8')).toContain('keyring-driver:intent permission-denied');
      expect(existsSync(intent)).toBe(true);
      chmodSync(intent, 0o600);
      expect(readFileSync(intent).equals(intentBytes)).toBe(true);
      await stop();
    } finally {
      chmodSync(intent, 0o600);
      rmSync(intent);
    }
    await start('k1', { k0: k0.toString('base64'), k1: k1.toString('base64') }, candidate, 'inventory-import');
    await expect.poll(() => child?.exitCode).toBe(0);
    expect(readFileSync(log, 'utf8')).toContain('keyring-driver:import absent=whole-refusal k0=0->2');
    await stop();
    await start('k1', { k1: k1.toString('base64'), k2: randomBytes(32).toString('base64') });
    await expect.poll(() => child?.exitCode).toBe(2);
    expect(readFileSync(log, 'utf8')).toContain("unconfigured key id 'k0' is required by 2 local wrappers");
    await stop();
    await start('k1', { k0: k0.toString('base64'), k1: k1.toString('base64') });
    await ready();
    await expect.poll(async () => (await (await fetch('http://127.0.0.1:18383/metrics')).text())
      .match(/terrabase_keys_last_complete[^\n]* ([1-9]\d*)/)?.[1]).toBeTruthy();
    await stop();
    await start('k1', { k0: k0.toString('base64'), k1: k1.toString('base64') }, candidate, 'inventory-trim');
    await expect.poll(() => child?.exitCode).toBe(0);
    await stop();
    await start('k1', { k0: k0.toString('base64'), k1: k1.toString('base64') });
    await rotated(paths.length + 2, 1);
    await stop();
    const catalogBytes = readFileSync(catalogKey);
    await start('k1', { k0: k0.toString('base64'), k1: randomBytes(32).toString('base64') });
    await expect.poll(() => child?.exitCode).toBe(2);
    expect(readFileSync(log, 'utf8')).toContain("catalog wrapper is not authentic under key id 'k1'");
    expect(readFileSync(catalogKey).equals(catalogBytes)).toBe(true);
    await stop();
    for (const material of [Buffer.alloc(31).toString('base64')]) {
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
    await rotated(paths.length + 2, 1);
    await stop();
    await start('k1', { k1: k1.toString('base64'), k2: randomBytes(32).toString('base64') });
    await ready();
    await stop();
    await rows('rows', 'k1');
    k0.fill(7);
    saveSecret('keyring-weak', k0.toString('base64'));
    for (const path of files(data)) wrap(path, 'k0');
    await start('k0', { k0: k0.toString('base64'), k1: k1.toString('base64') });
    await ready();
    const weakMetrics = await (await fetch('http://127.0.0.1:18383/metrics')).text();
    expect(weakMetrics).toMatch(/terrabase_keys_weak_in_use[^\n]*key_id="k0"[^\n]* 1(?:\n|$)/);
    expect(readFileSync(log, 'utf8').split('Weak master key in use').length - 1).toBe(1);
    expect(readFileSync(log, 'utf8')).toContain('k0');
    await stop();
    await start('k0', { k0: k0.toString('base64') }, candidate, undefined, join(directory, 'empty-weak'));
    await expect.poll(() => child?.exitCode).toBe(2);
    expect(readFileSync(log, 'utf8')).toContain("active master key id 'k0' consists of one repeated byte and has no local wrappers");
    await stop();
    const inspection = join(directory, 'inspection.json');
    const inspectionKey = randomBytes(32).toString('base64');
    saveSecret('keyring-inspection', inspectionKey);
    writeFileSync(inspection, JSON.stringify({ path: join(data, 'inspection', 'proof.promote.db'), key: inspectionKey }), { mode: 0o600 });
    await start('k0', { k0: k0.toString('base64') }, candidate, ['inspection-seed', inspection]);
    await expect.poll(() => readFileSync(log, 'utf8')).toContain('keyring-driver:inspection-seed nonempty-wal-ready');
    const killed = once(child!, 'exit');
    child!.kill('SIGKILL');
    await killed;
    await stop();
    await start('k0', { k0: k0.toString('base64') }, candidate, ['inspection', inspection]);
    await expect.poll(() => child?.exitCode).toBe(0);
    expect(readFileSync(log, 'utf8')).toContain('keyring-driver:inspection nonempty-wal bytes-identical success+failure; removed-duplicate=refused');
    await stop();
    console.log('keyring: partial catalog and tenant temporaries start and serve, unknown retirement count; unreadable tenant intent isolated; imports validate atomically and immediately count old credentials; weak existing key warns, empty weak node refuses; inspection preserves nonempty WAL and removed duplicate fails evidence');
  } finally {
    await stop();
    k0.fill(0);
    k1.fill(0);
  }
});

for (const mode of ['retained', 'missing-key', 'image', 'missing', 'null', 'number', 'empty', 'base64', 'version', 'authority-parent', 'artifact-decoy', 'scratch', 'cost-1', 'cost-2', 'cost-3']) {
  test('inventory contains persisted ' + mode, async () => {
    mkdirSync(stateDir, { recursive: true });
    const directory = mkdtempSync(join(stateDir, 'inventory-' + mode + '-'));
    const data = join(directory, mode === 'authority-parent' ? 'parent.creating' : 'data');
    const ring = join(directory, 'ring.json');
    const k0 = randomBytes(32).toString('base64');
    const k1 = randomBytes(32).toString('base64');
    const peer = randomBytes(32).toString('hex');
    for (const [name, value] of Object.entries({ k0, k1, peer })) saveSecret('inventory-' + mode + '-' + name, value);
    let child: ChildProcess | undefined;
    let log = '';
    let sequence = 0;
    const candidate = join(process.env.E2E_BUILD!, 'terrabase/terrabase.dll');
    const environment = Object.fromEntries(Object.entries(process.env).filter(([name]) =>
      !name.startsWith('TERRABASE_') && !name.startsWith('TS_') && !name.startsWith('AWS_') && name !== 'BUCKET_NAME'));
    async function stop(): Promise<void> {
      if (child?.exitCode === null && child.signalCode === null) {
        const exited = once(child, 'exit');
        child.kill('SIGTERM');
        await exited;
      }
      child = undefined;
    }
    function start(operation?: string, withoutOld = false): void {
      writeFileSync(ring, JSON.stringify({ active: mode.startsWith('cost-') ? 'k0' : 'k1', keys: withoutOld ? { k1, k2: randomBytes(32).toString('base64') } : { k0, k1 } }), { mode: 0o600 });
      log = join(directory, 'node-' + (++sequence) + '.log');
      const output = openSync(log, 'w', 0o600);
      child = spawn('dotnet', operation ? ['exec', '--depsfile', join(process.env.E2E_BUILD!, 'terrabase/terrabase.deps.json'),
        '--runtimeconfig', join(process.env.E2E_BUILD!, 'terrabase/terrabase.runtimeconfig.json'), process.env.E2E_KEYRING_DRIVER!,
        ...(operation === 'cost' ? ['inventory-cost'] : operation === 'trim' ? ['inventory-trim'] : ['inventory-seed', operation])] : [candidate], {
        env: { ...environment, DOTNET_PROCESSOR_COUNT: '4', DOTNET_ENVIRONMENT: 'Development',
          TERRABASE_MASTER_KEYS_FILE: ring, TERRABASE_PEER_SECRET: peer, TERRABASE_DATA_DIRECTORY: data,
          TERRABASE_MEMORY_BUDGET: '67108864', TERRABASE_STORAGE_THREADS: '1',
          TERRABASE_PORT: '18380', TERRABASE_INTERNAL_PORT: '18381', TERRABASE_POSTGRES_PORT: '18382', TERRABASE_METRICS_PORT: '18383' },
        stdio: ['ignore', output, output],
      });
      closeSync(output);
    }
    async function ready(): Promise<void> {
      await expect.poll(async () => {
        expect(child?.exitCode, log + '\n' + readFileSync(log, 'utf8').slice(-1500)).toBeNull();
        try { return (await fetch('http://127.0.0.1:18380/readyz')).status; } catch { return 0; }
      }).toBe(200);
    }
    try {
      start();
      await ready();
      await stop();
      if (mode.startsWith('cost-')) {
        start('cost');
        await expect.poll(() => child?.exitCode).toBe(0);
        const measured = readFileSync(log, 'utf8').split('\n').find(line => line.startsWith('keyring-driver:10000 '));
        expect(measured).toBeTruthy();
        console.log(mode + ': ' + measured);
        const references = Number(measured?.match(/first_reference=(-?\d+)/)?.[1]);
        expect(references === -1 || references >= 20_000).toBe(true);
        return;
      }
      if (mode === 'scratch') {
        mkdirSync(join(data, '.tmp/inspection/abandoned'), { recursive: true });
        writeFileSync(join(data, '.tmp/inspection/abandoned/copy.db'), 'private scratch');
        writeFileSync(join(data, 'outside-inspection'), 'keep');
        writeFileSync(join(data, '.tmp/outside-inspection'), 'keep sibling');
      } else if (mode === 'authority-parent' || mode === 'artifact-decoy') {
        const nonce = randomBytes(12);
        const cipher = createCipheriv('aes-256-gcm', Buffer.from(k0, 'base64'), nonce);
        const wrapped = Buffer.concat([nonce, cipher.update(randomBytes(32)), cipher.final(), cipher.getAuthTag()]).toString('base64');
        const name = mode === 'authority-parent' ? '01J00000000000000000000077.db.key' : 'artifact.db.key';
        const artifactDirectory = join(data, '01J00000000000000000000071', '01J00000000000000000000072');
        mkdirSync(artifactDirectory, { recursive: true });
        writeFileSync(join(artifactDirectory, name), 'TERRABASE-DB-KEY-1\n' + wrapped + '\n');
      } else {
        start(mode === 'missing-key' ? 'retained' : mode);
        await expect.poll(() => child?.exitCode, log).toBe(0);
        await stop();
      }
      start(undefined, ['missing-key', 'authority-parent', 'artifact-decoy'].includes(mode));
      if (mode === 'missing-key' || mode === 'authority-parent') {
        await expect.poll(() => child?.exitCode, log).toBe(2);
        expect(readFileSync(log, 'utf8')).toContain("unconfigured key id 'k0'");
      } else {
        await ready();
        const metrics = await (await fetch('http://127.0.0.1:18383/metrics')).text();
        if (mode === 'scratch') {
          expect(existsSync(join(data, '.tmp/inspection/abandoned'))).toBe(false);
          expect(readFileSync(join(data, 'outside-inspection'), 'utf8')).toBe('keep');
          expect(readFileSync(join(data, '.tmp/outside-inspection'), 'utf8')).toBe('keep sibling');
        } else if (mode === 'retained') {
          expect(metrics).toMatch(/terrabase_keys_local_wrapped[^\n]*key_id="k0"[^\n]*\} [1-9]\d*(?:\n|$)/);
          await stop();
          start('trim');
          await expect.poll(() => child?.exitCode).toBe(0);
          await stop();
          start();
          await ready();
          const trimmed = await (await fetch('http://127.0.0.1:18383/metrics')).text();
          expect(trimmed).toMatch(/terrabase_keys_local_wrapped[^\n]*key_id="k0"[^\n]*\} 0(?:\n|$)/);
        } else {
          expect(metrics).toMatch(/terrabase_keys_local_wrapped[^\n]*key_id="k0"[^\n]*\} -1(?:\n|$)/);
          if (mode !== 'artifact-decoy') expect(metrics).toMatch(/terrabase_keys_malformed(?:_total)?(?:\{[^\n]*\})? [1-9]\d*(?:\n|$)/);
        }
      }
      console.log('inventory actual: ' + mode + ' passed');
    } finally {
      await stop();
    }
  });
}
