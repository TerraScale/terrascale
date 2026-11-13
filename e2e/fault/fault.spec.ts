import { randomBytes } from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { readFileSync, renameSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect } from '@playwright/test';
import { test, database, json, name, project, removeDatabase, sql } from '../placement/support';
import { auth, dash, e2eRoot, stateDir } from '../support/stack';
import { keyId } from '../support/api-key';
import { journey, ownerSession, saveSecret } from '../support/state';
import { receiptHandoff } from './receipt-handoff';
import { crashWrite, endpoint, faultId, Ledger, nodes, nodeRequest, nodeStatus, regions, restartAfterKill, tokenReady, topology } from './support';

test.beforeEach(async () => {
  expect(process.env.E2E_TARGET ?? 'local').toBe('local');
  expect(process.env.E2E_REGIONS).toBe('2');
  expect(process.env.E2E_NODES_PER_REGION).toBe('2');
  await topology();
});

test('a writer killed during a write restarts without losing acknowledged rows in either region', async ({ cells, request }) => {
  const created = await json<{ id: string }>(await cells.post(`${endpoint('gru')}${project()}/databases`, {
    data: { name: name('fault'), regions: [...regions] },
  }), 201);
  const ledger = new Ledger(created.id);
  try {
    await sql(cells, endpoint('gru'), created.id, 'CREATE TABLE fault_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)');
    await ledger.write(cells, 'gru', [[1, 'before-gru']]);
    await ledger.compare(cells);
    await ledger.write(cells, 'fra', [[2, 'before-fra']]);
    await ledger.compare(cells);
    const retained = [...ledger.acknowledged];
    const current = journey();
    const grant = await json<{ access_token: string }>(await request.post(`${auth}/terrascale/oauth/token`, {
      form: { grant_type: 'client_credentials', client_id: keyId(current.apiKey), client_secret: current.apiKey, project: current.projectId, scope: 'sql:read sql:write databases:read databases:write' },
    }));
    saveSecret('fault-token', grant.access_token);
    const writer = (await topology()).find(node => node.region === 'gru')!;
    const writing = crashWrite(writer, ledger, grant.access_token);
    await writing.sent;
    const restart = restartAfterKill(writer, true, { token: grant.access_token, path: database(created.id) });
    try {
      const outcome = await writing.outcome;
      if (outcome.error) throw outcome.error;
    } finally {
      await restart();
    }
    await expect.poll(async () => {
      const response = await request.get(`http://127.0.0.1:${writer.port}/readyz`);
      return response.status();
    }, { message: 'the crashed writer recovers from its existing data directory' }).toBe(200);
    await ledger.compare(cells, retained);
    await ledger.write(cells, 'gru', [[3, 'after-gru']]);
    await ledger.write(cells, 'fra', [[4, 'after-fra']]);
    await ledger.compare(cells);
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('restore a backup into another database and compare its rows with the snapshot ledger in every region', async ({ cells }) => {
  const created = await json<{ id: string }>(await cells.post(`${endpoint('gru')}${project()}/databases`, {
    data: { name: name('backup-ledger'), regions: [...regions] },
  }), 201);
  const ledger = new Ledger(created.id);
  let restored: string | undefined;
  try {
    await sql(cells, endpoint('gru'), created.id, 'CREATE TABLE fault_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)');
    await ledger.write(cells, 'gru', [[1, 'snapshot-gru']]);
    await ledger.compare(cells);
    await ledger.write(cells, 'fra', [[2, 'snapshot-fra']]);
    await ledger.compare(cells);
    const snapshotRows = [...ledger.acknowledged];
    const backup = await json<{ id: string; status: string }>(await cells.post(`${endpoint('gru')}${database(created.id)}/backups`), 201);
    expect(backup.status).toBe('ready');
    ledger.record('snapshot', snapshotRows, 'gru');
    await ledger.write(cells, 'gru', [[3, 'after-snapshot']]);
    const operation = await json<{ id: string }>(await cells.post(`${endpoint('gru')}${database(created.id)}/backups/${backup.id}/restore`, {
      data: { name: name('restored-ledger') },
    }), 202);
    await expect.poll(async () => {
      const state = await json<{ status: string; database: string; errorCode?: string }>(await cells.get(`${endpoint('gru')}${database(created.id)}/restores/${operation.id}`));
      expect(state.status, state.errorCode).not.toBe('failed');
      if (state.status === 'completed') restored = state.database;
      return state.status;
    }, { message: 'the backup restore completes' }).toBe('completed');
    for (const region of regions) {
      await expect.poll(() => ledger.read(cells, region, restored), { message: `the restored ${region} data matches the snapshot`, timeout: 45_000, intervals: [250, 500, 1000] }).toEqual(snapshotRows);
    }
    await ledger.compare(cells);
    console.log(`backup ${backup.id}: restored snapshot ledger in both regions; later source write excluded`);
  } finally {
    if (restored) await removeDatabase(cells, restored);
    await removeDatabase(cells, created.id);
  }
});


test('handoff receives receipts with their original bindings before exporting them', async () => {
  const environment: NodeJS.ProcessEnv = { ...process.env, DOTNET_ENVIRONMENT: 'Development', ASPNETCORE_ENVIRONMENT: 'Development' };
  for (const key of Object.keys(environment)) {
    if (key.startsWith('TS_') || key.startsWith('TERRABASE_')) delete environment[key];
  }
  const master = randomBytes(32).toString('base64');
  saveSecret('receipt-import-master', master);
  environment.TERRABASE_MASTER_KEY = master;
  const build = process.env.E2E_BUILD ?? join(stateDir, 'build');
  const result = await promisify(execFile)('dotnet', [join(build, 'receipt-import', 'terrabase.dll'),
    `--data-directory=${join(stateDir, `receipt-import-${faultId()}`)}`,
  ], { env: environment, cwd: e2eRoot, timeout: 20_000 });
  expect(result.stdout).toContain('RECEIPT_CASE absent-refusal passed');
  expect(result.stdout).toContain('RECEIPT_CASE closure-version-two passed');
  expect(result.stdout).toContain('RECEIPT_IMPORT identical=accepted invalid=16 retained_bytes=2 ordinary_export=0');
  console.log(result.stdout.split('\n').filter(line => line.startsWith('RECEIPT_')).join('\n'));
});

test('same-region retained receiver preserves its copy and resumes after restoration', async () => {
  await receiptHandoff('retained');
});

for (const scenario of ['purge-holder', 'purge-authority', 'closure-holder', 'closure-authority', 'stale-closure', 'stale-refusal',
  'absent-holder', 'absent-authority', 'absent-database', 'absent-project', 'conflict-count', 'rows', 'bytes', 'identical']) {
  test(`receipt handoff ${scenario} preserves evidence and serving authority`, async () => {
    await receiptHandoff(scenario);
  });
}

test('another region outside admitted peers cannot interrupt local writer renewal', async ({ cells, request, playwright }) => {
  const writers = await topology();
  const local = writers.find(node => node.region === 'gru')!;
  const foreign = writers.find(node => node.region === 'fra')!;
  const target = nodes.find(node => node.region === 'fra' && node.name !== foreign.name)!;
  const user = faultId();
  const manage = async (node: typeof foreign, method: string, path: string, body?: unknown, expected = 200) => {
    const response = await nodeRequest(node, path, method, body, user);
    expect(response.status, response.body).toBe(expected);
    return response.body ? JSON.parse(response.body) as { id: string } : undefined;
  };
  const organization = (await manage(foreign, 'POST', '/v1/management/organizations', { name: name('handoff-closure') }, 201))!;
  const organizationPath = `/v1/management/organizations/${organization.id}`;
  const projectRecord = (await manage(foreign, 'POST', `${organizationPath}/projects`, { name: name('handoff-closure') }, 201))!;
  const projectPath = `${organizationPath}/projects/${projectRecord.id}`;
  const retired = (await manage(foreign, 'POST', `/v1/projects/${projectRecord.id}/databases`, { name: name('handoff-purge'), regions: ['fra'] }, 201))!;
  await manage(foreign, 'DELETE', `/v1/projects/${projectRecord.id}/databases/${retired.id}`, undefined, 204);
  const peers = join(stateDir, 'peers');
  const original = readFileSync(peers, 'utf8');
  const replacePeers = (value: string) => {
    writeFileSync(`${peers}.next`, value);
    renameSync(`${peers}.next`, peers);
  };
  const created = await json<{ id: string }>(await cells.post(`${endpoint('gru')}${project()}/databases`, {
    data: { name: name('owner-isolation'), regions: ['gru'] },
  }), 201);
  const ledger = new Ledger(created.id);
  const metric = async () => {
    const response = await request.get(`http://${local.address}:${local.port + 1011}/metrics`);
    expect(response.status()).toBe(200);
    const text = await response.text();
    return text.split('\n').filter(line => line.startsWith('terrabase_ownership_refresh_errors_total{') && line.includes('region="fra"'))
      .reduce((total, line) => total + Number(line.split(' ').at(-1)), 0);
  };
  try {
    await sql(cells, endpoint('gru'), created.id, 'CREATE TABLE fault_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)');
    await expect.poll(() => nodeStatus(local, 'fra')).toMatchObject({ owner: foreign.name });
    const before = await metric();
    const log = join(stateDir, 'logs', `${local.service}.log`);
    const offset = readFileSync(log).length;
    const inconsistent = original.trim().split('\n').filter(line => !line.startsWith(`${foreign.name} `))
      .sort((a, b) => a.split(' ')[1].localeCompare(b.split(' ')[1])).join('\n') + '\n';
    replacePeers(inconsistent);
    const started = Date.now();
    let row = 0;
    while (Date.now() - started < 30_000) {
      expect(await nodeStatus(local), 'the same local writer remains ready throughout the foreign fault')
        .toMatchObject({ owner: local.name, writer: true, ready: true });
      for (const node of nodes.filter(node => node.region === 'gru' && node.name !== local.name)) {
        expect(await nodeStatus(node)).toMatchObject({ writer: false, ready: true });
      }
      const ready = await request.get(`http://${local.address}:${local.port}/readyz`);
      expect(ready.status()).toBe(200);
      await ledger.write(cells, 'gru', [[++row, 'uninterrupted']]);
    }
    expect(row).toBeGreaterThan(1);
    expect(await metric()).toBeGreaterThan(before);
    const failures = readFileSync(log).subarray(offset).toString().split('\n')
      .filter(line => line.includes('Regional ownership observation failed for ') && line.includes('fra'));
    expect(failures, 'unchanged foreign failure logs once').toHaveLength(1);
    expect(await nodeStatus(foreign), 'invalid local authority still stops its own writer').toMatchObject({ writer: false });
    expect(await nodeStatus(local, 'fra')).toMatchObject({ owner: foreign.name });
    console.log(`foreign ownership fault: ${Date.now() - started} ms, ${row} acknowledged local writes, fra counter increased`);
    replacePeers(original);
    await expect.poll(() => nodeStatus(foreign), { message: 'the restored foreign writer reacquires local admission' })
      .toMatchObject({ writer: true, ready: true });
    await topology();
    const drain = await nodeRequest(foreign, `/v1/rollout/drain?target=${target.name}`, 'POST');
    expect(drain.status, drain.body).toBe(204);
    await expect.poll(() => nodeStatus(local, 'fra'), { message: 'the restored foreign owner is observed after handoff' })
      .toMatchObject({ owner: target.name });
    await expect.poll(() => nodeStatus(target)).toMatchObject({ owner: target.name, writer: true, ready: true });
    expect(await ledger.read(cells, 'gru')).toEqual(ledger.acknowledged);
    console.log(`foreign ownership restored: ${target.name} observed from ${local.name}; acknowledged rows retained`);
  } finally {
    replacePeers(original);
    await expect.poll(() => nodeStatus(foreign), { message: 'discover the committed foreign writer even when drain failed' })
      .toMatchObject({ ready: true });
    let observedOwner = '';
    await expect.poll(async () => {
      const status = await nodeStatus(foreign);
      observedOwner = status.owner;
      const owner = nodes.find(node => node.name === status.owner && node.region === foreign.region);
      return owner ? nodeStatus(owner) : undefined;
    }, { message: 'the actual foreign writer settles before restoration' }).toMatchObject({ writer: true, ready: true });
    const actualWriter = nodes.find(node => node.name === observedOwner && node.region === foreign.region);
    expect(actualWriter, 'the actual foreign writer is admitted').toBeDefined();
    if (actualWriter!.name !== foreign.name) {
      await restartAfterKill(foreign, false)();
      const primed = await nodeRequest(foreign, `/v1/rollout/prime?source=${actualWriter!.name}`, 'POST');
      expect(primed.status, primed.body).toBe(204);
      const restored = await nodeRequest(actualWriter!, `/v1/rollout/drain?target=${foreign.name}`, 'POST');
      expect(restored.status, restored.body).toBe(204);
      await restartAfterKill(target, false)();
      const caughtUp = await nodeRequest(target, `/v1/rollout/prime?source=${foreign.name}`, 'POST');
      expect(caughtUp.status, caughtUp.body).toBe(204);
    }
    await expect.poll(() => nodeStatus(local), { message: 'local authority is restored before cleanup' })
      .toMatchObject({ writer: true, ready: true });
    await removeDatabase(cells, created.id);
    expect(readFileSync(peers, 'utf8')).toBe(original);
    const restoredWriters = await topology();
    expect(restoredWriters.map(node => node.name).sort()).toEqual(writers.map(node => node.name).sort());
    for (const node of nodes) {
      for (const writer of writers) {
        expect(await nodeStatus(node, writer.region)).toMatchObject({ owner: writer.name, ready: true });
      }
    }
    for (const node of [foreign, target]) await tokenReady(node, cells, `${project()}/databases`);
    const closure = { version: 2, closureId: faultId(), authorityOrganizationId: organization.id };
    const closurePath = `/admin/projects/${projectRecord.id}/closure`;
    const closing = await nodeRequest(local, closurePath, 'PUT', closure);
    expect([200, 202]).toContain(closing.status);
    const query = `?version=2&closureId=${closure.closureId}&authorityOrganizationId=${organization.id}`;
    await expect.poll(async () => {
      const response = await nodeRequest(foreign, closurePath + query);
      expect([200, 202]).toContain(response.status);
      return JSON.parse(response.body);
    }, { message: 'closure completes after returning to the original receipt holder' })
      .toMatchObject({ status: 'accepted', pendingHolders: 0, closureId: closure.closureId });
    await manage(foreign, 'DELETE', projectPath, undefined, 204);
    await manage(foreign, 'DELETE', organizationPath, undefined, 204);
    for (const region of regions) expect((await cells.get(`${endpoint(region)}${project()}/databases`)).status()).toBe(200);
    const owner = await playwright.request.newContext({ storageState: ownerSession() });
    try {
      const profile = await owner.get(`${dash}/profile`);
      expect(new URL(profile.url()).pathname).toBe('/profile');
      expect(profile.status()).toBe(200);
      expect(await profile.text()).toContain(journey().email);
    } finally {
      await owner.dispose();
    }
    console.log('restored original writers, all four peer observations, and existing account credentials and session');
  }
});
