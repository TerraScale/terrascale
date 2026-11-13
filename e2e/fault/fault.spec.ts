import { readFileSync, renameSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect } from '@playwright/test';
import { test, database, json, name, project, removeDatabase, sql } from '../placement/support';
import { auth, stateDir } from '../support/stack';
import { journey, saveSecret } from '../support/state';
import { crashWrite, endpoint, Ledger, nodes, nodeRequest, nodeStatus, regions, restartAfterKill, topology } from './support';

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
      form: { grant_type: 'client_credentials', client_id: current.apiKey.slice(0, 26), client_secret: current.apiKey, project: current.projectId, scope: 'sql:read sql:write databases:read databases:write' },
    }));
    saveSecret('fault-token', grant.access_token);
    const writer = (await topology()).find(node => node.region === 'gru')!;
    const writing = crashWrite(writer, ledger, grant.access_token);
    await writing.sent;
    const restart = restartAfterKill(writer);
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


test('another region outside admitted peers cannot interrupt local writer renewal', async ({ cells, request }) => {
  const writers = await topology();
  const local = writers.find(node => node.region === 'gru')!;
  const foreign = writers.find(node => node.region === 'fra')!;
  const target = nodes.find(node => node.region === 'fra' && node.name !== foreign.name)!;
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
    await expect.poll(() => nodeStatus(local), { message: 'local authority is restored before cleanup' })
      .toMatchObject({ writer: true, ready: true });
    await removeDatabase(cells, created.id);
  }
});
