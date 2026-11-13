import { randomBytes } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { connect } from 'node:http2';
import { join } from 'node:path';
import { test, expect } from '@playwright/test';
import { production, stateDir } from '../support/stack';

test.skip(production, 'clock delivery requires the throwaway internal listener');

const alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
function id(): string {
  let time = Date.now();
  let head = '';
  for (let index = 0; index < 10; index++) {
    head = alphabet[time % 32] + head;
    time = Math.floor(time / 32);
  }
  return head + Array.from(randomBytes(16), (value) => alphabet[value % 32]).join('');
}
type Answer = { status: number; body: string };

async function internal(method: string, path: string, body?: unknown, user?: string): Promise<Answer> {
  const credential = readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[0];
  const session = connect('http://127.0.0.1:8081');
  try {
    return await new Promise<Answer>((resolve, reject) => {
      session.on('error', reject);
      const payload = body === undefined ? undefined : JSON.stringify(body);
      const stream = session.request({
        ':method': method, ':path': path, 'x-terrabase-peer-credential': credential,
        ...(payload === undefined ? {} : { 'content-type': 'application/json', 'content-length': Buffer.byteLength(payload) }),
        ...(user ? { 'x-terrabase-user-id': user } : {}),
      });
      let status = 0;
      let text = '';
      stream.setEncoding('utf8');
      stream.on('response', (headers) => { status = Number(headers[':status']); });
      stream.on('data', (chunk: string) => { text += chunk; });
      stream.on('error', reject);
      stream.on('end', () => resolve({ status, body: text }));
      stream.end(payload);
    });
  } finally {
    session.close();
  }
}

async function json(method: string, path: string, body: unknown, user?: string, expected = 200) {
  const response = await internal(method, path, body, user);
  expect(response.status, response.body).toBe(expected);
  return JSON.parse(response.body);
}

test('future remote stamps apply once without advancing the local clock beyond its bound', async () => {
  const user = id();
  const organization = await json('POST', '/v1/management/organizations', { name: `Clock ${id()}` }, user, 201);
  const organizationPath = `/v1/management/organizations/${organization.id}`;
  let projectPath: string | undefined;
  let databasePath: string | undefined;
  try {
    const project = await json('POST', `${organizationPath}/projects`, { name: 'Clock delivery' }, user, 201);
    projectPath = `${organizationPath}/projects/${project.id}`;
    const created = await json('POST', `/v1/projects/${project.id}/databases`, { name: 'clock-delivery', allRegions: true }, user, 201);
    databasePath = `/v1/projects/${project.id}/databases/${created.id}`;
    const sql = (statement: string) => json('POST', `${databasePath}/sql/execute`, { statements: [{ sql: statement }] });
    await sql('CREATE TABLE clock_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)');
    const applyPath = `/v1/sync/organizations/${organization.id}/projects/${project.id}/databases/${created.id}/apply`;
    const supersededCount = async () => {
      const metrics = await internal('GET', '/metrics');
      expect(metrics.status).toBe(200);
      const match = /^terrabase_home_apply_superseded_total\{[^}]*\bregion="fra"[^}]*\} (\S+)/m.exec(metrics.body);
      return match ? Number(match[1]) : 0;
    };
    const baseline = await supersededCount();
    const current = {
      id: id(), region: 'fra',
      hlc: { physicalMilliseconds: Date.now() - 10_000, logical: 0, nodeId: 'clock-remote' },
      changes: [{ op: 'insert', table: 'clock_rows', values: { id: 3, value: 'current' } }],
    };
    expect(await json('POST', applyPath, current)).toMatchObject({ state: 'confirmed', conflicts: [] });
    const older = {
      ...current, id: id(), hlc: { ...current.hlc, physicalMilliseconds: current.hlc.physicalMilliseconds - 1000 },
      changes: [{ op: 'update', table: 'clock_rows', key: { id: 3 }, values: { value: 'older' } }],
    };
    const lost = await json('POST', applyPath, older);
    expect(lost).toMatchObject({ state: 'confirmed', conflicts: [{ kind: 'superseded' }] });
    await expect.poll(supersededCount).toBe(baseline + 1);
    expect(await json('POST', applyPath, older)).toEqual(lost);
    const transaction = {
      id: id(), region: 'fra',
      hlc: { physicalMilliseconds: Date.now() + 86_400_000, logical: 7, nodeId: 'clock-remote' },
      changes: [{ op: 'insert', table: 'clock_rows', values: { id: 1, value: 'delivered' } }],
    };
    const applied = await json('POST', applyPath, transaction);
    expect(applied).toMatchObject({ id: transaction.id, state: 'confirmed', conflicts: [] });
    expect(await json('POST', applyPath, transaction)).toEqual(applied);
    await expect.poll(async () => {
      const metrics = await internal('GET', '/metrics');
      expect(metrics.status).toBe(200);
      const ahead = /^terrabase_clock_hlc_ahead_milliseconds(?:\{[^}]*\})? (\S+)/m.exec(metrics.body);
      const capped = /^terrabase_clock_observe_capped_total(?:\{[^}]*\})? (\S+)/m.exec(metrics.body);
      return ahead && capped && Number(capped[1]) >= 1 && Number(ahead[1]) <= 50;
    }, { message: 'the receiver reports capped observations and a bounded HLC' }).toBe(true);
    expect(await supersededCount()).toBe(baseline + 1);
    const at = Date.now() - 5_000;
    const change = (row: number, physicalMilliseconds: number, op: string, values?: Record<string, unknown>) => ({
      id: id(), region: 'fra', hlc: { physicalMilliseconds, logical: 0, nodeId: 'clock-remote' },
      changes: [op === 'insert' ? { op, table: 'clock_rows', values: { id: row, ...values } } : { op, table: 'clock_rows', key: { id: row }, ...(values ? { values } : {}) }],
    });
    for (const row of [4, 5]) {
      expect(await json('POST', applyPath, change(row, at, 'insert', { value: 'inserted' }))).toMatchObject({ state: 'confirmed', conflicts: [] });
    }
    expect(await json('POST', applyPath, change(4, at + 200, 'delete'))).toMatchObject({ state: 'confirmed', conflicts: [] });
    expect(await json('POST', applyPath, change(4, at + 100, 'update', { value: 'older than the delete' })))
      .toMatchObject({ state: 'confirmed', conflicts: [{ kind: 'superseded' }] });
    await expect.poll(supersededCount, { message: 'an update older than the tombstone is superseded and counted' }).toBe(baseline + 2);
    expect(await json('POST', applyPath, change(5, at + 100, 'delete'))).toMatchObject({ state: 'confirmed', conflicts: [] });
    expect(await json('POST', applyPath, change(5, at + 200, 'update', { value: 'newer than the delete' })))
      .toMatchObject({ state: 'reverted', conflicts: [{ kind: 'missing_row' }] });
    expect(await supersededCount()).toBe(baseline + 2);
    await sql("INSERT INTO clock_rows VALUES (2, 'origin still writes')");
    expect((await sql('SELECT id, value FROM clock_rows ORDER BY id')).results[0].rows).toEqual([[1, 'delivered'], [2, 'origin still writes'], [3, 'current']]);
  } finally {
    for (const path of [databasePath, projectPath, organizationPath]) {
      if (path) {
        const removed = await internal('DELETE', path, undefined, user);
        expect(removed.status, removed.body).toBe(204);
      }
    }
  }
});
