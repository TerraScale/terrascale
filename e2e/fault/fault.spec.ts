import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { natsRequest } from '../retention/nats.mjs';
import { expect } from '@playwright/test';
import { test, database, json, name, project, removeDatabase, sql } from '../placement/support';
import { auth, stateDir } from '../support/stack';
import { journey, saveSecret } from '../support/state';
import { crashWrite, endpoint, Ledger, nodes, regions, restartAfterKill, topology } from './support';

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

test('retained deletion holds an incoming remote write until restoration', async ({ cells }, info) => {
  info.annotations.push({ type: 'release-dependency', description: 'Expected to fail with the acknowledging consumer; requires the frontier consumer to hold unknown retained IDs. This remains a required fault journey assertion.' });
  const created = await json<{ id: string }>(await cells.post(`${endpoint('gru')}${project()}/databases`, {
    data: { name: name('retained-incoming'), regions: [...regions] },
  }), 201);
  const ledger = new Ledger(created.id);
  const token = readFileSync(join(stateDir, 'nats-token'), 'utf8').trim();
  const stream = 'TERRABASE_PUSH_FRA';
  const listed = await natsRequest(token, '$JS.API.CONSUMER.NAMES.' + stream) as { consumers: string[] };
  const consumers = listed.consumers.filter(consumer => /^home-gru(?:-|$)/.test(consumer));
  expect(consumers.length).toBeGreaterThan(0);
  const pause = async (until: string) => {
    for (const consumer of consumers) await natsRequest(token, '$JS.API.CONSUMER.PAUSE.' + stream + '.' + consumer, { pause_until: until });
  };
  let restored = false;
  try {
    await sql(cells, endpoint('gru'), created.id, 'CREATE TABLE fault_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)');
    await ledger.write(cells, 'gru', [[1, 'before-delete']]);
    await ledger.compare(cells);
    await pause(new Date(Date.now() + 3_600_000).toISOString());
    await ledger.write(cells, 'fra', [[2, 'arrived-while-retained']]);
    const state = await natsRequest(token, '$JS.API.STREAM.INFO.' + stream) as { state: { last_seq: number } };
    expect((await ledger.read(cells, 'gru')).some(row => row[0] === 2)).toBe(false);
    expect((await cells.delete(`${endpoint('gru')}${database(created.id)}`)).status()).toBe(204);
    await pause('0001-01-01T00:00:00Z');
    await expect.poll(async () => {
      const details = await Promise.all(consumers.map(consumer => natsRequest(token, '$JS.API.CONSUMER.INFO.' + stream + '.' + consumer))) as { delivered: { stream_seq: number } }[];
      return details.some(consumer => consumer.delivered.stream_seq >= state.state.last_seq);
    }, { message: 'the remote write reaches the consumer while the home database is retained' }).toBe(true);
    await expect.poll(async () => {
      const details = await Promise.all(consumers.map(consumer => natsRequest(token, '$JS.API.CONSUMER.INFO.' + stream + '.' + consumer))) as { ack_floor: { stream_seq: number } }[];
      if (details.some(consumer => consumer.ack_floor.stream_seq >= state.state.last_seq)) return true;
      const homeNodes = nodes.filter(node => node.region === 'gru');
      const metrics = await Promise.all(homeNodes.map(async node => {
        const response = await fetch('http://' + node.address + ':' + (node.name === 'journey-1' ? 9091 : 9291) + '/metrics');
        return response.text();
      }));
      return metrics.some(text => /^terrabase_push_held(?:\{[^}]*\})? [1-9]/m.test(text));
    }, { message: 'delivery is either acknowledged by the old consumer or held by the frontier consumer before restore' }).toBe(true);
    const restore = await cells.post(`${endpoint('gru')}${project()}/deleted-databases/${created.id}/restore`);
    expect(restore.status()).toBe(200);
    restored = true;
    await expect.poll(() => ledger.read(cells, 'gru'), { message: 'restore includes the remote write delivered during retention', timeout: 45_000 })
      .toEqual([[1, 'before-delete'], [2, 'arrived-while-retained']]);
  } finally {
    await pause('0001-01-01T00:00:00Z');
    if (restored) await removeDatabase(cells, created.id);
  }
});
