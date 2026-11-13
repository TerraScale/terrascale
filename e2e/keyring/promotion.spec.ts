import { spawn, type ChildProcess } from 'node:child_process';
import { createDecipheriv, randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { closeSync, existsSync, linkSync, mkdtempSync, openSync, readFileSync, renameSync, rmSync, watch, writeFileSync } from 'node:fs';
import { connect } from 'node:http2';
import { join } from 'node:path';
import { test as base, expect } from '@playwright/test';
import { stateDir } from '../support/stack';
import { saveSecret } from '../support/state';

type Promotion = {
  beforePublication: () => Promise<void>;
  afterPublication: () => Promise<void>;
  failedImport: () => Promise<void>;
};

const test = base.extend<{}, { promotion: Promotion }>({
  promotion: [async ({}, use) => {
    const directory = mkdtempSync(join(stateDir, 'keyring-promotion-'));
    const peers = join(directory, 'peers');
    writeFileSync(peers, 'keyring-home gru 127.0.0.1\nkeyring-replica fra 127.0.0.2\n');
    const peer = randomBytes(32).toString('hex');
    const k0 = randomBytes(32);
    const k1 = randomBytes(32);
    saveSecret('promotion-peer', peer);
    saveSecret('promotion-k0', k0.toString('base64'));
    saveSecret('promotion-k1', k1.toString('base64'));
    const [access, secret] = readFileSync(join(stateDir, 'r2'), 'utf8').split('\n');
    saveSecret('promotion-store-access', access);
    saveSecret('promotion-store-secret', secret);
    const children = new Set<ChildProcess>();
    const observers = new Set<ReturnType<typeof watch>>();
    const home = 'http://127.0.0.1:18481';
    const replica = 'http://127.0.0.2:18481';
    const environment = Object.fromEntries(Object.entries(process.env).filter(([name]) =>
      !name.startsWith('TERRABASE_') && !name.startsWith('TS_') && !name.startsWith('AWS_') && name !== 'BUCKET_NAME'));

    function start(region: 'gru' | 'fra', active: 'k0' | 'k1', binary = join(process.env.E2E_BUILD!, 'terrabase', 'terrabase.dll'), requestFile?: string): ChildProcess {
      const ring = join(directory, region + '-ring.json');
      writeFileSync(ring, JSON.stringify({ active, keys: active === 'k0'
        ? { k0: k0.toString('base64') } : { k0: k0.toString('base64'), k1: k1.toString('base64') } }), { mode: 0o600 });
      const output = openSync(join(directory, region + '-' + active + '.log'), 'w', 0o600);
      const arguments_ = requestFile ? ['exec', '--depsfile', join(process.env.E2E_BUILD!, 'terrabase', 'terrabase.deps.json'),
        '--runtimeconfig', join(process.env.E2E_BUILD!, 'terrabase', 'terrabase.runtimeconfig.json'), binary, requestFile] : [binary];
      const child = spawn('dotnet', arguments_, {
        env: { ...environment, DOTNET_PROCESSOR_COUNT: '4', DOTNET_ENVIRONMENT: 'Production',
          TERRABASE_AUTH_ISSUER: 'https://auth.terrascale.tech/terrascale', TERRABASE_AUTH_AUDIENCE: 'https://api.terrascale.tech',
          AWS_ENDPOINT_URL_S3: 'https://journey.backups.terrascale.tech', AWS_REGION: 'auto', BUCKET_NAME: 'journey-backups',
          AWS_ACCESS_KEY_ID: access, AWS_SECRET_ACCESS_KEY: secret,
          TERRABASE_SHAPES_ACCESS_KEY_ID: access, TERRABASE_SHAPES_SECRET_ACCESS_KEY: secret, TERRABASE_SHAPES_CDN_API_TOKEN: peer,
          ...(binary === process.env.E2E_KEYRING_BASELINE ? { TERRABASE_MASTER_KEY: k0.toString('base64') } : { TERRABASE_MASTER_KEYS_FILE: ring }), TERRABASE_PEER_SECRET: peer,
          TERRABASE_DATA_DIRECTORY: join(directory, region), TERRABASE_MEMORY_BUDGET: '67108864', TERRABASE_STORAGE_THREADS: '1',
          TS_REGION: region, TS_NODE: region === 'gru' ? 'keyring-home' : 'keyring-replica', TS_PEERS_FILE: peers,
          TERRABASE_INTERNAL_PORT: '18481', TERRABASE_PORT: region === 'gru' ? '18480' : '18490',
          TERRABASE_POSTGRES_PORT: region === 'gru' ? '18482' : '18492', TERRABASE_METRICS_PORT: region === 'gru' ? '18483' : '18493',
        }, stdio: [requestFile ? 'pipe' : 'ignore', output, output],
      });
      closeSync(output);
      children.add(child);
      return child;
    }

    async function stop(child: ChildProcess): Promise<void> {
      if (child.exitCode === null && child.signalCode === null) {
        const exited = once(child, 'exit');
        child.stdin?.end();
        child.kill('SIGTERM');
        await exited;
      }
      children.delete(child);
    }

    async function ready(port: number, clock = true): Promise<void> {
      await expect.poll(async () => {
        for (const child of children) expect(child.exitCode, 'throwaway nodes remain running').toBeNull();
        try { return (await fetch('http://127.0.0.1:' + port + '/readyz')).status; } catch { return 0; }
      }).toBe(200);
      if (clock) {
        await expect.poll(async () => (await (await fetch('http://127.0.0.1:' + (port + 3) + '/metrics')).text())
          .match(/^terrabase_clock_local_writes_allowed(?:\{[^\n]*\})? (\d+)$/m)?.[1]).toBe('1');
      }
    }

    function request(address: string, method: string, path: string, body?: unknown, actAs = true): Promise<Record<string, unknown>> {
      return new Promise((resolve, reject) => {
        const session = connect(address);
        session.on('error', reject);
        const stream = session.request({ ':method': method, ':path': path, 'content-type': 'application/json',
          'x-terrabase-peer-credential': peer, ...(actAs ? { 'x-terrabase-user-id': '01J00000000000000000000000' } : {}),
        });
        let status = 0;
        let text = '';
        stream.setEncoding('utf8');
        stream.on('response', headers => { status = Number(headers[':status']); });
        stream.on('data', (chunk: string) => { text += chunk; });
        stream.on('error', error => { session.close(); reject(error); });
        stream.on('end', () => {
          session.close();
          if (![200, 201, 204].includes(status)) {
            const failure = JSON.parse(text) as { error?: { code?: string; message?: string } };
            reject(new Error(method + ' ' + path + ': ' + status + ' ' + failure.error?.code + ' ' + failure.error?.message));
          }
          else resolve(text ? JSON.parse(text) as Record<string, unknown> : {});
        });
        stream.end(body ? JSON.stringify(body) : undefined);
      });
    }

    function observePromotion(organization: unknown, project: unknown, database: unknown): string {
      const parent = join(directory, 'fra', '.replicas', String(organization), String(project));
      const name = String(database) + '.promote.db.key';
      const observed = join(directory, String(database) + '.observed');
      const observer = watch(parent, (_event, file) => {
        if (file !== name || existsSync(observed)) return;
        try { linkSync(join(parent, name), observed); }
        catch (error) {
          if (!(error instanceof Error && 'code' in error && error.code === 'ENOENT')) throw error;
        }
      });
      observers.add(observer);
      return observed;
    }

    function unwrap(record: Buffer): Buffer {
      const wrapped = record.toString('ascii').split('\n')[1];
      const parts = wrapped.split('.');
      const body = Buffer.from(parts.length === 3 ? parts[2] : wrapped, 'base64');
      const cipher = createDecipheriv('aes-256-gcm', parts[1] === 'k1' ? k1 : k0, body.subarray(0, 12));
      if (parts.length === 3) cipher.setAAD(Buffer.from('v2.' + parts[1]));
      cipher.setAuthTag(body.subarray(-16));
      return Buffer.concat([cipher.update(body.subarray(12, -16)), cipher.final()]);
    }

    try {
      let first = start('gru', 'k0');
      let second = start('fra', 'k0');
      await ready(18480);
      await ready(18490);
      const organization = await request(home, 'POST', '/v1/management/organizations', { name: 'keyring-promotion' });
      const project = await request(home, 'POST', '/v1/management/organizations/' + String(organization.id) + '/projects', { name: 'promotion' });
      await request(home, 'PUT', '/admin/projects/' + String(project.id) + '/enrollment', { organization: organization.id }, false);
      await request(home, 'PUT', '/v1/management/organizations/' + String(organization.id) + '/plan', { plan: 'pro' });
      const projectPath = '/v1/projects/' + String(project.id);
      const syncPath = (id: unknown) => '/v1/sync/organizations/' + String(organization.id) + '/projects/' + String(project.id) + '/databases/' + String(id);
      const one = await request(home, 'POST', projectPath + '/databases', { name: 'one', homeRegion: 'gru', replicaRegions: ['fra'] });
      const two = await request(home, 'POST', projectPath + '/databases', { name: 'two', homeRegion: 'gru', replicaRegions: ['fra'] });
      const active = await request(home, 'POST', projectPath + '/databases', { name: 'active', homeRegion: 'gru', replicaRegions: ['fra'] });
      const crash = await request(home, 'POST', projectPath + '/databases', { name: 'crash', homeRegion: 'gru', replicaRegions: ['fra'] });
      const published = await request(home, 'POST', projectPath + '/databases', { name: 'published', homeRegion: 'gru', replicaRegions: ['fra'] });
      const failed = await request(home, 'POST', projectPath + '/databases', { name: 'failed', homeRegion: 'gru', replicaRegions: ['fra'] });
      await stop(second);
      second = start('fra', 'k0');
      await ready(18490);
      const replicaKey = (id: unknown) => join(directory, 'fra', '.replicas', String(organization.id), String(project.id), String(id) + '.db.key');
      const promotedKey = (id: unknown) => join(directory, 'fra', String(organization.id), String(project.id), String(id) + '.db.key');
      for (const database of [one, two, active, crash, published, failed]) {
        await request(home, 'POST', projectPath + '/databases/' + String(database.id) + '/sql/execute', {
          statements: [{ sql: 'CREATE TABLE keyring_rows(value INTEGER)' }, { sql: 'INSERT INTO keyring_rows VALUES(' + ([one, two].includes(database) ? '42' : '0') + ')' }],
        }, false);
      }
      const sql = { statements: [{ sql: 'SELECT value FROM keyring_rows' }] };
      await expect.poll(() => existsSync(replicaKey(one.id)) && existsSync(replicaKey(two.id))).toBe(true);
      await request(replica, 'POST', syncPath(one.id) + '/catch-up', undefined, false);
      await expect.poll(async () => {
        try { return (await request(replica, 'POST', projectPath + '/databases/' + String(one.id) + '/sql/execute', sql, false)).results; }
        catch (error) { return { error: error instanceof Error ? error.message : 'request failed' }; }
      }).toMatchObject([{ rows: [[42]] }]);
      const original = readFileSync(replicaKey(one.id), 'ascii').split('\n')[1];
      const oldRecord = readFileSync(replicaKey(two.id));
      if (process.env.E2E_KEYRING_BASELINE) {
        await stop(second);
        second = start('fra', 'k0', process.env.E2E_KEYRING_BASELINE);
        await ready(18490, false);
        await request(replica, 'POST', syncPath(one.id) + '/catch-up', undefined, false);
        expect((await request(replica, 'POST', projectPath + '/databases/' + String(one.id) + '/sql/execute', sql, false)).results).toMatchObject([{ rows: [[42]] }]);
        expect(readFileSync(replicaKey(two.id)).equals(oldRecord)).toBe(true);
        console.log('one key creation: origin/main opened new replica records unchanged and read row=42');
        await stop(second);
        second = start('fra', 'k0');
        await ready(18490);
      }
      const firstPlacement = await request(home, 'GET', syncPath(one.id) + '/placement', undefined, false);
      const firstObserved = observePromotion(organization.id, project.id, one.id);
      await request(replica, 'POST', syncPath(one.id) + '/catch-up', undefined, false);
      await request(replica, 'POST', syncPath(one.id) + '/promote', {
        moveId: '01J00000000000000000000001', fromRegion: 'gru', version: Number(firstPlacement.placementVersion) + 1,
        replicaRegions: ['gru'], allRegions: false, name: firstPlacement.name, identity: firstPlacement.identity,
      }, false);
      expect(existsSync(firstObserved)).toBe(true);
      expect(readFileSync(firstObserved, 'ascii').split('\n')[1] === original).toBe(true);
      rmSync(firstObserved);
      expect(existsSync(replicaKey(one.id))).toBe(false);
      expect((await request(replica, 'POST', projectPath + '/databases/' + String(one.id) + '/sql/execute', sql, false)).results).toMatchObject([{ rows: [[42]] }]);
      console.log('one key promotion: promoted wrapper byte-identical; source removed after durable destination; SQL opens');
      await stop(second);
      await stop(first);
      first = start('gru', 'k1');
      second = start('fra', 'k1');
      await ready(18480);
      await ready(18490);
      await expect.poll(async () => (await (await fetch('http://127.0.0.1:18493/metrics')).text())
        .match(/terrabase_keys_last_complete[^\n]* (\d+)/)?.[1] ?? '0').not.toBe('0');
      expect(readFileSync(replicaKey(two.id), 'ascii').startsWith('TERRABASE-REPLICA-KEY-2\nv2.k1.')).toBe(true);
      writeFileSync(replicaKey(two.id) + '.retained', oldRecord, { mode: 0o600 });
      renameSync(replicaKey(two.id) + '.retained', replicaKey(two.id));
      await request(replica, 'POST', syncPath(two.id) + '/catch-up', undefined, false);
      await expect.poll(async () => {
        try { return (await request(replica, 'POST', projectPath + '/databases/' + String(two.id) + '/sql/execute', sql, false)).results; }
        catch (error) { return { error: error instanceof Error ? error.message : 'request failed' }; }
      }).toMatchObject([{ rows: [[42]] }]);
      const secondPlacement = await request(home, 'GET', syncPath(two.id) + '/placement', undefined, false);
      const secondObserved = observePromotion(organization.id, project.id, two.id);
      await request(replica, 'POST', syncPath(two.id) + '/catch-up', undefined, false);
      await request(replica, 'POST', syncPath(two.id) + '/promote', {
        moveId: '01J00000000000000000000002', fromRegion: 'gru', version: Number(secondPlacement.placementVersion) + 1,
        replicaRegions: ['gru'], allRegions: false, name: secondPlacement.name, identity: secondPlacement.identity,
      }, false);
      expect(readFileSync(promotedKey(two.id), 'ascii').startsWith('TERRABASE-DB-KEY-2\nv2.k1.')).toBe(true);
      expect(existsSync(secondObserved)).toBe(true);
      expect(readFileSync(secondObserved, 'ascii').startsWith('TERRABASE-DB-KEY-2\nv2.k1.')).toBe(true);
      const originalPlain = unwrap(oldRecord);
      const promotedPlain = unwrap(readFileSync(secondObserved));
      expect(promotedPlain.equals(originalPlain)).toBe(true);
      originalPlain.fill(0);
      promotedPlain.fill(0);
      rmSync(secondObserved);
      expect(existsSync(replicaKey(two.id))).toBe(false);
      expect((await request(replica, 'POST', projectPath + '/databases/' + String(two.id) + '/sql/execute', sql, false)).results).toMatchObject([{ rows: [[42]] }]);
      console.log('two keys promotion: replica scan and old-key promotion both rewrapped; verified destination opens; source removed');
      console.log('verification refusal: no reachable wrap-corruption input without a product switch; helper failure paths checked by code reading');
      expect(process.env.E2E_KEYRING_DRIVER, 'lifecycle driver is required').toBeTruthy();
      {
        await stop(second);
        const driverRequest = join(directory, 'lifecycle.json');
        const detached = (id: unknown) => replicaKey(id).replace('.db.key', '.promote.db');
        const driverLog = join(directory, 'fra-k1.log');
        const checkpoint = async (marker: string) => {
          await expect.poll(() => readFileSync(driverLog, 'utf8').includes(marker)).toBe(true);
        };
        writeFileSync(driverRequest, JSON.stringify({ organization: organization.id, project: project.id, database: active.id }), { mode: 0o600 });
        second = start('fra', 'k1', process.env.E2E_KEYRING_DRIVER!, driverRequest);
        await checkpoint('keyring-driver:replica-ready');
        await stop(first);
        second.stdin!.write('detach\n');
        await checkpoint('keyring-driver:detached');
        expect(existsSync(detached(active.id))).toBe(true);
        expect(existsSync(detached(active.id) + '.key')).toBe(true);
        second.stdin!.write('pass\n');
        await checkpoint('keyring-driver:survived');
        await checkpoint('keyring-driver:nonwriter wrapped=-1 snapshot_age=-1 last_complete=0; became writer during local scan');
        expect(existsSync(detached(active.id))).toBe(true);
        expect(existsSync(detached(active.id) + '.key')).toBe(true);
        second.stdin!.write('import\n');
        await checkpoint('keyring-driver:imported row=42');
        await stop(second);
        console.log('active promotion: paused after detach with former home stopped; whole multi-key pass preserved both files; import read row=42');
        console.log('writer transition: started non-writer, became writer during local scan; wrapped=-1 snapshot_age=-1 last_complete=0');
        const launch = async (database: Record<string, unknown>) => {
          writeFileSync(driverRequest, JSON.stringify({ organization: organization.id, project: project.id, database: database.id }), { mode: 0o600 });
          second = start('fra', 'k1', process.env.E2E_KEYRING_DRIVER!, driverRequest);
          await checkpoint('keyring-driver:replica-ready');
          second.stdin!.write('detach\n');
          await checkpoint('keyring-driver:detached');
        };
        const maintenance = async () => {
          second.stdin!.write('pass\n');
          await checkpoint('keyring-driver:nonwriter');
        };
        const kill = async () => {
          const exited = once(second, 'exit');
          second.kill('SIGKILL');
          await exited;
          children.delete(second);
        };
        const beforePublication = async () => {
          await launch(crash);
          await kill();
          expect(existsSync(detached(crash.id))).toBe(true);
          await launch(crash);
          expect(existsSync(replicaKey(crash.id))).toBe(true);
          await maintenance();
          second.stdin!.write('import\n');
          await checkpoint('keyring-driver:imported row=42');
          await stop(second);
          console.log('crash before publication: home stopped; replica-only row=42; restart put back database, sidecars and key; new promotion read 42');

        };
        const afterPublication = async () => {
          await launch(published);
          await maintenance();
          second.stdin!.write('publish\n');
          await checkpoint('keyring-driver:published');
          expect(existsSync(promotedKey(published.id))).toBe(true);
          expect(existsSync(detached(published.id))).toBe(true);
          await kill();
          writeFileSync(driverRequest, JSON.stringify({ organization: organization.id, project: project.id, database: published.id, mode: 'published' }), { mode: 0o600 });
          second = start('fra', 'k1', process.env.E2E_KEYRING_DRIVER!, driverRequest);
          await checkpoint('keyring-driver:unregistered-survived row=42');
          expect(existsSync(detached(published.id))).toBe(false);
          expect(existsSync(detached(published.id) + '.key')).toBe(false);
          await stop(second);
          console.log('crash after publication: home stopped; destination opened once with row=42; restart removed only the redundant detached source; missing catalog registration preserved the destination after its grace period');

        };
        const failedImport = async () => {
          await launch(failed);
          await maintenance();
          second.stdin!.write('fail\n');
          await checkpoint('keyring-driver:put-back row=42');
          expect(existsSync(replicaKey(failed.id))).toBe(true);
          expect(existsSync(detached(failed.id))).toBe(false);
          await stop(second);
          console.log('failed import: home stopped; cancelled import put back a usable replica; direct replica read=42');
        };
        await use({ beforePublication, afterPublication, failedImport });
      }
    } finally {
      for (const observer of observers) observer.close();
      for (const child of children) await stop(child);
      k0.fill(0);
      k1.fill(0);
    }
  }, { scope: 'worker' }],
});

test.describe.configure({ mode: 'serial' });

test('a crash before publication restores the only surviving replica', async ({ promotion }) => {
  await promotion.beforePublication();
});

test('a crash after publication removes only the redundant source', async ({ promotion }) => {
  await promotion.afterPublication();
});

test('a failed import puts back a readable replica', async ({ promotion }) => {
  await promotion.failedImport();
});
