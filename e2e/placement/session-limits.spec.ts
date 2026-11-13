import { once } from 'node:events';
import { expect } from '@playwright/test';
import { rawSqlSocket, frame } from '../support/sql-socket';
import { production } from '../support/stack';
import { journey, saveSecret } from '../support/state';
import { test, home, project, database, name, json, removeDatabase, type Database } from './support';

test.skip(production || process.env.E2E_REGIONS !== '2', 'placement requires the local stack started with E2E_REGIONS=2');

test('a relayed session finishes a bounded close and releases its process slot', async ({ cells, request }) => {
  const created = await json<Database>(await cells.post(`${home}${project()}/databases`, {
    data: { name: name('session-relay'), homeRegion: 'gru', replicaRegions: [] },
  }), 201);
  const url = 'http://127.0.0.1:8180' + database(created.id) + '/sql/ws';
  const active = async () => {
    const response = await request.get('http://127.0.0.1:9191/metrics');
    expect(response.status()).toBe(200);
    const metric = (await response.text()).split('\n').find(line => /^terrabase_qos_relay_sessions(?:\{| )/.test(line));
    expect(metric).toBeDefined();
    return Number(metric!.split(' ').at(-1));
  };
  try {
    for (let sample = 0; sample < 3; sample++) {
      const ws = await rawSqlSocket(url);
      try {
        const close = await ws.closing;
        expect(close).toEqual({ code: 1008, reason: 'no auth frame within 5 seconds' });
        expect(await active()).toBe(1);
        const started = performance.now();
        await ws.closed;
        expect(performance.now() - started).toBeLessThan(3000);
        expect(await active()).toBe(0);
      } finally {
        ws.socket.destroy();
        await ws.closed;
      }
    }
    const current = journey();
    const token = await request.post('https://auth.terrascale.tech/terrascale/oauth/token', {
      form: {
        grant_type: 'client_credentials', client_id: current.apiKey.slice(0, 26),
        client_secret: current.apiKey, project: current.projectId, scope: 'sql:read sql:write',
      },
    });
    expect(token.status()).toBe(200);
    const credential = (await token.json()).access_token as string;
    saveSecret('relay-token', credential);
    const ws = await rawSqlSocket(url, credential);
    try {
      const result = once(ws.events, 'message');
      ws.socket.write(frame(1, Buffer.from(JSON.stringify({ id: 1, type: 'execute', statements: [{ sql: 'SELECT 42' }] }))));
      expect((await result)[0].results[0].rows).toEqual([[42]]);
      ws.socket.write(frame(8, Buffer.from([3, 232])));
      await ws.closed;
    } finally {
      ws.socket.destroy();
      await ws.closed;
    }
  } finally {
    await removeDatabase(cells, created.id);
  }
});
