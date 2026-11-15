import { once } from 'node:events';
import { readFileSync } from 'node:fs';
import { connect } from 'node:http2';
import { join } from 'node:path';
import { expect } from '@playwright/test';
import { extendPeers, startRegion, type RegionProcess } from './fleet';
import { production, stateDir } from '../support/stack';
import { journey } from '../support/state';
import { test, home, replica, project, database, name, json, sql, removeDatabase, type Cells } from './support';

test.skip(production || process.env.E2E_REGIONS !== '2' || process.env.E2E_NODES_PER_REGION !== '1',
  'one node in each of two regions, with port 8380 free for sjc');

test('snapshot and point-in-time restore run in fra', async ({ cells }) => {
  await publish('pro');
  await restorePair(cells, replica, 'fra');
});

test('snapshot and point-in-time restore run in sjc', async ({ cells }) => {
  const restorePeers = extendPeers(['sjc']);
  let node: RegionProcess | undefined;
  try {
    node = await startRegion('sjc', 0);
    await publish('pro');
    await restorePair(cells, node.endpoint, 'sjc', node.endpoint);
  } finally {
    if (node) await node.stop();
    restorePeers();
  }
});

async function restorePair(cells: Cells, endpoint: string, region: string, removeEndpoint = '') {
  const source = await json<{ id: string }>(await cells.post(`${endpoint}${project()}/databases`, {
    data: { name: name('backup-' + region), regions: [region, 'gru'] },
  }), 201);
  const copies: string[] = [];
  try {
    await sql(cells, endpoint, source.id, 'CREATE TABLE rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)');
    await sql(cells, endpoint, source.id, "INSERT INTO rows (id, value) VALUES (1, 'snapshot')");
    await served(cells, source.id, [[1, 'snapshot']]);
    const backup = await json<{ id: string; status: string }>(await cells.post(`${endpoint}${database(source.id)}/backups`), 201);
    expect(backup.status).toBe('ready');
    const elsewhere = await cells.post(`${home}${database(source.id)}/backups/${backup.id}/restore`, {
      data: { name: name('snapshot-' + region) },
    });
    expect(elsewhere.status(), await elsewhere.text()).toBe(421);
    expect(elsewhere.headers()['ts-replay']).toBe('region=' + region);
    const snapshot = await json<{ id: string }>(await cells.post(`${endpoint}${database(source.id)}/backups/${backup.id}/restore`, {
      data: { name: name('snapshot-copy-' + region) },
    }), 202);
    copies.push(await completed(cells, endpoint, source.id, snapshot.id));
    expect(await rows(cells, endpoint, copies[0])).toEqual([[1, 'snapshot']]);

    await sql(cells, endpoint, source.id, "INSERT INTO rows (id, value) VALUES (2, 'later')");
    const acked = Date.now();
    await served(cells, source.id, [[1, 'snapshot'], [2, 'later']]);
    let at = 0;
    await expect.poll(async () => {
      const window = await cells.get(`${endpoint}${database(source.id)}/point-in-time`);
      if (window.status() !== 200) return 0;
      at = Date.parse((await window.json() as { latest: string }).latest);
      return at;
    }, { message: region + ' proves the later write' }).toBeGreaterThanOrEqual(acked);
    const when = new Date(at).toISOString();
    const missed = await cells.post(`${home}${database(source.id)}/point-in-time/restore`, {
      data: { pointInTime: when, name: name('time-' + region) },
    });
    expect(missed.status(), await missed.text()).toBe(421);
    expect(missed.headers()['ts-replay']).toBe('region=' + region);
    const timed = await json<{ id: string }>(await cells.post(`${endpoint}${database(source.id)}/point-in-time/restore`, {
      data: { pointInTime: when, name: name('time-copy-' + region) },
    }), 202);
    copies.push(await completed(cells, endpoint, source.id, timed.id));
    expect(await rows(cells, endpoint, copies[1])).toEqual([[1, 'snapshot'], [2, 'later']]);
    console.log(`${region}: snapshot ${backup.id} and point-in-time ${when} restored on that region; gru answered 421 region=${region}`);
  } finally {
    for (const id of copies) await remove(cells, removeEndpoint || home, id, !removeEndpoint);
    await remove(cells, removeEndpoint || home, source.id, !removeEndpoint);
  }
}

async function served(cells: Cells, id: string, expected: (number | string)[][]) {
  await expect.poll(async () => {
    const response = await cells.post(`${home}${database(id)}/sql/execute`, {
      data: { statements: [{ sql: 'SELECT id, value FROM rows ORDER BY id' }] },
    });
    if (response.status() !== 200 || response.headers()['x-terrabase-region'] !== 'gru') return null;
    return (await response.json() as { results: { rows: (number | string)[][] }[] }).results[0].rows;
  }, { message: 'gru holds the placed copy' }).toEqual(expected);
}

async function completed(cells: Cells, endpoint: string, source: string, operation: string) {
  let copy = '';
  await expect.poll(async () => {
    const state = await json<{ status: string; database: string; errorCode?: string }>(
      await cells.get(`${endpoint}${database(source)}/restores/${operation}`));
    expect(state.status, state.errorCode).not.toBe('failed');
    if (state.status === 'completed') copy = state.database;
    return state.status;
  }, { message: 'the restore completes' }).toBe('completed');
  return copy;
}

async function rows(cells: Cells, endpoint: string, id: string) {
  return (await sql(cells, endpoint, id, 'SELECT id, value FROM rows ORDER BY id')).results[0].rows;
}

async function remove(cells: Cells, endpoint: string, id: string, followHome: boolean) {
  if (followHome) {
    await removeDatabase(cells, id);
    return;
  }
  await expect.poll(async () => {
    const response = await cells.delete(`${endpoint}${database(id)}`);
    if (response.status() === 409 || response.status() === 429) return false;
    expect(response.status(), await response.text()).toBe(204);
    return true;
  }, { message: 'delete the database on its home' }).toBe(true);
}

async function publish(plan: string) {
  const credential = readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[0];
  const session = connect('http://127.0.0.1:8081');
  const stream = session.request({
    ':method': 'PUT', ':path': `/admin/organizations/${journey().organizationId}/plan`,
    'x-terrabase-peer-credential': credential, 'content-type': 'application/json',
  });
  session.on('error', error => stream.destroy(error));
  try {
    const response = once(stream, 'response');
    stream.end(JSON.stringify({ plan }));
    const [headers] = await response;
    let body = '';
    stream.setEncoding('utf8');
    for await (const chunk of stream) body += chunk;
    expect(Number(headers[':status']), body).toBe(200);
  } finally {
    session.close();
  }
}
