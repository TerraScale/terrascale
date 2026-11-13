import { randomBytes } from 'node:crypto';
import { execFileSync, spawn } from 'node:child_process';
import { once } from 'node:events';
import { closeSync, mkdtempSync, openSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { connect } from 'node:http2';
import { request as httpRequest } from 'node:http';
import { extendPeers, startRegion, type RegionProcess } from './fleet';
import { basename, join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { expect, type APIResponse, type Page } from '@playwright/test';
import { test, authenticatedCells, home, replica, project, database, name, json, sql, replicated, removeDatabase, type Cells } from './support';
import { auth, dash, production, stateDir } from '../support/stack';
import { Audit, auditedContext } from '../support/audit';
import { journey, ownerSession, saveCookies, saveSecret } from '../support/state';
import { confirmWithPassword, onboard, signUp, verify } from '../support/hosted';
import { newAddress } from '../support/inbox';
import { forgedHeaders, issueKey, sourceRefusals, unanswered } from './forwarding';
import { createApiKey } from '../support/paid';
import { signInToken } from '../support/auth-management';

test.skip(production || process.env.E2E_REGIONS !== '2', 'placement requires the local stack started with E2E_REGIONS=2');
test.describe.configure({ mode: 'default', timeout: 180_000 });

type Database = { id: string; regions: string[] };
type Observation = { region: string; role: string | null; revision: string | null; lastPulledAt: string | null; lagMs: number | null };
type Progress = { regions: Observation[] };

async function answer(response: APIResponse, code: number, documented: string): Promise<void> {
  expect(response.status(), response.status() === code ? documented : `${documented}: ${await response.text()}`).toBe(code);
  if (code >= 400) expect((await response.json()).error.code, documented).toBe(documented);
}

function files(directory: string, id: string): string[] {
  return (readdirSync(join(stateDir, directory), { recursive: true }) as string[]).filter(path => basename(path).includes(id));
}

async function create(cells: Cells, regions: string[], projectId?: string): Promise<Database> {
  const response = await cells.post(`${home}${project(projectId)}/databases`, { data: { name: name('regions'), regions } });
  await answer(response, 201, '201 database created');
  return response.json();
}

async function progress(cells: Cells, id: string, region = home, projectId?: string): Promise<Progress> {
  const response = await cells.get(`${region}${database(id, projectId)}/regions`);
  await answer(response, 200, '200 database regions');
  return response.json();
}

async function observed(cells: Cells, id: string, projectId?: string): Promise<void> {
  await expect.poll(async () => (await progress(cells, id, home, projectId)).regions.find(row => row.region === 'fra'),
    { message: 'fra has a completed pull observation', timeout: 45_000, intervals: [250, 500, 1000] })
    .toMatchObject({ revision: expect.any(String), lastPulledAt: expect.any(String), lagMs: expect.any(Number) });
}

async function seed(cells: Cells, id: string, projectId?: string): Promise<void> {
  await sql(cells, home, id, 'CREATE TABLE placement_rows (value INTEGER PRIMARY KEY)', projectId);
  await sql(cells, home, id, 'INSERT INTO placement_rows VALUES (1), (2), (3)', projectId);
}

let followedHints = 0;
test.afterEach(() => console.log('placement management 421 hints followed: ' + followedHints));

async function patch(cells: Cells, id: string, regions: string[], projectId?: string): Promise<APIResponse> {
  let response = await cells.patch(`${home}${database(id, projectId)}`, { data: { regions } });
  if (response.status() === 421) {
    followedHints++;
    await answer(response, 421, 'wrong_region');
    response = await cells.patch(`https://${response.headers()['x-terrabase-database-home']}.api.terrascale.tech${database(id, projectId)}`, { data: { regions } });
  }
  return response;
}

async function move(cells: Cells, id: string, label: string, timeout: number, statuses: number[] = [], projectId?: string): Promise<void> {
  try {
    await expect.poll(async () => {
      const response = await patch(cells, id, ['fra'], projectId);
      statuses.push(response.status());
      if (response.status() === 200) {
        await answer(response, 200, '200 database updated');
        expect((await response.json()).regions).toEqual(['fra']);
        return true;
      }
      await answer(response, 503, 'replica_not_ready');
      return false;
    }, { message: `${label} reaches 200`, timeout, intervals: [1000] }).toBe(true);
  } finally {
    console.log(`${label} statuses: ${JSON.stringify(statuses)}`);
  }
  expect((await progress(cells, id, replica, projectId)).regions.map(row => row.region)).toEqual(['fra']);
}

test('R1 create two regions, read across listeners and observe a pull', async ({ cells }) => {
  const created = await create(cells, ['gru', 'fra']);
  try {
    expect(created.regions).toEqual(['fra', 'gru']);
    await seed(cells, created.id);
    await replicated(cells, replica, created.id, 3);
    await observed(cells, created.id);
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('R2 add a region and read a row committed before the addition', async ({ cells }) => {
  const created = await create(cells, ['gru']);
  try {
    await seed(cells, created.id);
    const added = await patch(cells, created.id, ['fra', 'gru']);
    await answer(added, 200, '200 database updated with writer retained');
    expect((await added.json()).regions).toEqual(['fra', 'gru']);
    await observed(cells, created.id);
    await replicated(cells, replica, created.id, 3);
  } finally {
    await removeDatabase(cells, created.id);
  }
});

async function headerRows(page: Page, url: string): Promise<number[][]> {
  return page.evaluate(url => new Promise<number[][]>((resolve, reject) => {
    const socket = new WebSocket(url, 'terrascale.sql.v1');
    socket.onopen = () => socket.send(JSON.stringify({ id: 1, type: 'execute', statements: [{ sql: 'SELECT value FROM placement_rows ORDER BY value' }] }));
    socket.onerror = () => reject(new Error('header-authenticated session failed'));
    socket.onmessage = message => {
      const answer = JSON.parse(String(message.data));
      socket.close();
      if (answer.type === 'error') reject(new Error(JSON.stringify(answer.error)));
      else resolve(answer.results[0].rows);
    };
    socket.onclose = () => reject(new Error('header-authenticated session closed before answering'));
  }), url);
}

type Meter = { requests: number; operations: number; received: number; sent: number };
type Usage = { items: { region: string; series: { requests: Record<string, number>; operations: Record<string, number>; clientBandwidth: { bytesReceived: number; bytesSent: number } }[] }[] };

async function usageAt(cells: Cells, id: string, region: string): Promise<Meter> {
  const readings = await Promise.all([home, replica].map(async url =>
    json<Usage>(await cells.get(url + project() + '/analytics?database=' + id + '&region=' + region + '&range=1h'))));
  const usage = { items: readings.flatMap(reading => reading.items) };
  return usage.items.filter(item => item.region === region).flatMap(item => item.series).reduce((sum, point) => ({
    requests: sum.requests + Object.values(point.requests).reduce((a, b) => a + b, 0),
    operations: sum.operations + Object.values(point.operations).reduce((a, b) => a + b, 0),
    received: sum.received + point.clientBandwidth.bytesReceived,
    sent: sum.sent + point.clientBandwidth.bytesSent,
  }), { requests: 0, operations: 0, received: 0, sent: 0 });
}

async function meteredOnce(cells: Cells, id: string, protocol: string, work: () => Promise<unknown>, owner: 'gru' | 'fra' = 'fra'): Promise<void> {
  const before = { gru: await usageAt(cells, id, 'gru'), fra: await usageAt(cells, id, 'fra') };
  await work();
  let after = before;
  await expect.configure({ soft: true }).poll(async () => {
    after = { gru: await usageAt(cells, id, 'gru'), fra: await usageAt(cells, id, 'fra') };
    return { gru: after.gru.requests - before.gru.requests, fra: after.fra.requests - before.fra.requests };
  }, { message: `${protocol} is metered as one destination request` }).toEqual(owner === 'fra' ? { gru: 0, fra: 1 } : { gru: 1, fra: 0 });
  const other = owner === 'fra' ? 'gru' : 'fra';
  expect.soft(after[other], protocol + ' records no intermediate requests, units or bytes').toEqual(before[other]);
  expect.soft(after[owner].sent - before[owner].sent).toBeGreaterThan(0);
  if (['REST', 'WebSocket', 'Postgres'].includes(protocol)) expect.soft(after[owner].operations - before[owner].operations).toBe(1);
  console.log('R3 usage ' + protocol + ': ' + JSON.stringify(Object.fromEntries(['gru', 'fra'].map(region => [region, Object.fromEntries(Object.keys(before[owner]).map(meter => [meter, after[region as 'gru' | 'fra'][meter as keyof Meter] - before[region as 'gru' | 'fra'][meter as keyof Meter]]))]))));
}

function postgresRows(id: string): string {
  const current = journey();
  return execFileSync('psql', ['-X', '-tA', '-v', 'ON_ERROR_STOP=1', '-c', 'SELECT value FROM placement_rows ORDER BY value',
    `postgresql://any@pg.terrascale.tech:5432/${current.projectId}/${id}?sslmode=verify-full&sslrootcert=${encodeURIComponent(join(stateDir, 'pki', 'ca.pem'))}`], {
    env: { PATH: process.env.PATH, SSL_CERT_FILE: process.env.SSL_CERT_FILE, PGPASSWORD: current.apiKey,
      PGSSLCERT: join(stateDir, 'postgres-client.crt'), PGSSLKEY: join(stateDir, 'postgres-client.key') },
    encoding: 'utf8',
  }).trim();
}

async function acceptedSockets(request: Cells): Promise<number> {
  const response = await request.get('http://127.0.0.1:9191/metrics');
  expect(response.status()).toBe(200);
  const row = (await response.text()).split('\n').find(line => line.startsWith('terrabase_sql_connections_total{') && line.includes('region="fra"'));
  return row ? Number(row.slice(row.lastIndexOf(' ') + 1)) : 0;
}

test('R3 replace: authorization ends at the source before discovery or connection', async ({ cells, request, page, browser }) => {
  const current = journey();
  const restricted = await create(cells, ['gru']);
  const databaseKey = await issueKey(current.organizationId, current.projectId, restricted.id);
  const projectKey = await issueKey('00000000000000000000000001', '00000000000000000000000002');
  await removeDatabase(cells, restricted.id);
  const created = await named(cells, replica, name('source-auth'), ['fra'], current.projectId);
  try {
    await sourceRefusals(cells, request, page, created.id, { project: projectKey, database: databaseKey });
    const forged = await cells.post(home + database(created.id) + '/sql/execute', {
      headers: forgedHeaders, data: { statements: [{ sql: 'SELECT 1' }] },
    });
    await answer(forged, 200, '200 customer headers cannot mark a request internal or shorten its deadline');
    expect(forged.headers()['x-terrabase-region']).toBe('fra');
    const context = await auditedContext(browser, { storageState: ownerSession() });
    try {
      const owner = await signInToken(context.request, 'placement-ip-owner');
      const issued = await json<{ key: string }>(await context.request.post(auth + '/v1/terrascale/organizations/' + current.organizationId + '/projects/' + current.projectId + '/api-keys', {
        headers: { authorization: 'Bearer ' + owner },
        data: { name: 'placement IP refusal', scopes: ['sql:read'], allowed_cidrs: ['192.0.2.1/32'] },
      }), 201);
      saveSecret('placement-ip-key', issued.key);
      const grant = await json<{ access_token: string }>(await request.post(auth + '/terrascale/oauth/token', {
        form: { grant_type: 'client_credentials', client_id: issued.key.slice(0, 26), client_secret: issued.key, project: current.projectId, scope: 'sql:read' },
      }));
      saveSecret('placement-ip-token', grant.access_token);
      await sourceRefusals(cells, request, page, created.id, {}, grant.access_token);
    } finally {
      await saveCookies(context);
      await context.close();
    }
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('R3 replace the writer and proxy REST and WebSocket through a removed region', async ({ cells, request, page }) => {
  const created = await create(cells, ['gru', 'fra']);
  let pg: ReturnType<typeof spawn> | undefined;
  let pgExited: Promise<unknown[]> | undefined;
  try {
    await seed(cells, created.id);
    await observed(cells, created.id);
    expect((await progress(cells, created.id, replica)).regions.map(row => row.region)).toEqual(['fra', 'gru']);
    const current = journey();
    const grant = await json<{ access_token: string }>(await request.post(`${auth}/terrascale/oauth/token`, {
      form: {
        grant_type: 'client_credentials',
        client_id: current.apiKey.slice(0, 26),
        client_secret: current.apiKey,
        project: current.projectId,
        scope: 'sql:read sql:write databases:read',
      },
    }));
    saveSecret('placement-socket-token', grant.access_token);
    await page.setExtraHTTPHeaders({ Authorization: `Bearer ${grant.access_token}`, 'X-TerraScale-Region-Pin': 'gru' });
    await page.evaluate(url => new Promise<void>((resolve, reject) => {
      const socket = new WebSocket(url, 'terrascale.sql.v1');
      const state = window as Window & { writerClosed?: Promise<number> };
      state.writerClosed = new Promise<number>(closed => { socket.onclose = event => closed(event.code); });
      socket.onerror = () => reject(new Error('header-authenticated session failed'));
      socket.onopen = () => socket.send(JSON.stringify({ id: 1, type: 'execute', statements: [{ sql: 'SELECT value FROM placement_rows ORDER BY value' }] }));
      socket.onmessage = message => {
        const answer = JSON.parse(String(message.data));
        if (answer.type === 'error') reject(new Error(JSON.stringify(answer.error)));
        else resolve();
      };
    }), `${home.replace('https:', 'wss:')}${database(created.id)}/sql/ws`);
    pg = spawn('psql', ['-X', '-v', 'ON_ERROR_STOP=1', '-v', 'VERBOSITY=verbose',
      `postgresql://any@pg.terrascale.tech:5432/${current.projectId}/${created.id}?sslmode=verify-full&sslrootcert=${encodeURIComponent(join(stateDir, 'pki', 'ca.pem'))}`], {
      env: { PATH: process.env.PATH, SSL_CERT_FILE: process.env.SSL_CERT_FILE, PGPASSWORD: current.apiKey,
      PGSSLCERT: join(stateDir, 'postgres-client.crt'), PGSSLKEY: join(stateDir, 'postgres-client.key') },
      stdio: ['pipe', 'pipe', 'pipe'],
    });
    pgExited = new Promise<unknown[]>(resolve => pg!.once('close', (...args) => resolve(args)));
    let pgOutput = '';
    pg.stdout!.setEncoding('utf8');
    pg.stderr!.setEncoding('utf8');
    pg.stderr!.on('data', chunk => { pgOutput += chunk; });
    const opened = new Promise<void>((resolve, reject) => {
      pg!.stdout!.on('data', chunk => {
        pgOutput += chunk;
        if (pgOutput.includes('writer_session_open')) resolve();
      });
      pg!.on('error', reject);
      pg!.on('exit', () => reject(new Error('Postgres session exited before the writer move: ' + pgOutput)));
    });
    pg.stdin!.write("SELECT 'writer_session_open';\n");
    await opened;
    const duringMove = await Promise.all([
      move(cells, created.id, 'R3', 60_000),
      cells.get(`${replica}${database(created.id)}/regions`),
    ]);
    expect([200, 503]).toContain(duringMove[1].status());
    if (duringMove[1].status() === 503) await answer(duringMove[1], 503, 'unavailable');
    expect(await page.evaluate(() => (window as Window & { writerClosed?: Promise<number> }).writerClosed)).toBe(1012);
    pg.stdin!.end('SELECT value FROM placement_rows ORDER BY value;\n');
    await pgExited;
    expect(pgOutput).toContain('FATAL');
    expect(pgOutput).toContain('57P01');
    console.log('R3 old Postgres session: FATAL 57P01 administrative shutdown');
    console.log(`R3 writer move: concurrent regions read ${duringMove[1].status()}; old header-authenticated socket closed 1012`);
    await sql(cells, replica, created.id, 'INSERT INTO placement_rows VALUES (4)');
    await expect.poll(async () => {
      const response = await cells.post(`${home}${database(created.id)}/sql/execute`, { data: { statements: [{ sql: 'SELECT value FROM placement_rows ORDER BY value' }] } });
      await answer(response, 200, '200 proxied through a non-member listener');
      return { region: response.headers()['x-terrabase-region'], rows: (await response.json()).results[0].rows };
    }, { message: 'the non-member listener proxies to fra', timeout: 45_000, intervals: [250, 500, 1000] })
      .toEqual({ region: 'fra', rows: [[1], [2], [3], [4]] });
    const headerResult = await headerRows(page, `${home.replace('https:', 'wss:')}${database(created.id)}/sql/ws`);
    expect(headerResult).toEqual([[1], [2], [3], [4]]);
    await page.setExtraHTTPHeaders({});
    const result = await page.evaluate(({ url, token }) => new Promise<{ rows: number[][] }>((resolve, reject) => {
      const socket = new WebSocket(url, 'terrascale.sql.v1');
      socket.onopen = () => socket.send(JSON.stringify({ id: 1, type: 'auth', token }));
      socket.onerror = () => reject(new Error('proxied WebSocket connection failed'));
      socket.onmessage = message => {
        const answer = JSON.parse(String(message.data));
        if (answer.type === 'error') {
          socket.close();
          reject(new Error(JSON.stringify(answer.error)));
        } else if (answer.id === 1) {
          socket.send(JSON.stringify({ id: 2, type: 'execute', statements: [{ sql: 'SELECT value FROM placement_rows ORDER BY value' }] }));
        } else if (answer.id === 2) {
          socket.close();
          resolve({ rows: answer.results[0].rows });
        }
      };
      socket.onclose = () => reject(new Error('proxied WebSocket closed before answering'));
    }), { url: `${home.replace('https:', 'wss:')}${database(created.id)}/sql/ws`, token: grant.access_token });
    expect(result.rows).toEqual([[1], [2], [3], [4]]);
    console.log('R3 nonmember REST: 200 from fra, rows [[1],[2],[3],[4]]; WebSocket header-credential and auth-frame queries: same rows');
    await expect.poll(() => acceptedSockets(request), { message: "destination has counted both authenticated proxy sessions" }).toBeGreaterThanOrEqual(2);
    const connectionsBefore = await acceptedSockets(request);
    const refusedAnonymous = await page.evaluate(url => new Promise<{ error: string; close: number }>((resolve, reject) => {
      const socket = new WebSocket(url, 'terrascale.sql.v1');
      let error = '';
      socket.onopen = () => socket.send(JSON.stringify({ id: 1, type: 'auth', token: 'invalid-credential' }));
      socket.onmessage = message => { error = JSON.parse(String(message.data)).error.code; };
      socket.onclose = event => resolve({ error, close: event.code });
      socket.onerror = () => reject(new Error('the anonymous upgrade did not reach local authentication'));
    }), `${home.replace('https:', 'wss:')}${database(created.id)}/sql/ws`);
    expect(refusedAnonymous).toEqual({ error: 'unauthorized', close: 1008 });
    expect(await acceptedSockets(request)).toBe(connectionsBefore);
    const missingCredentialClose = await page.evaluate(url => new Promise<number>((resolve, reject) => {
      const socket = new WebSocket(url, 'terrascale.sql.v1');
      socket.onclose = event => resolve(event.code);
      socket.onerror = () => reject(new Error('the local anonymous upgrade failed'));
    }), `${home.replace('https:', 'wss:')}${database(created.id)}/sql/ws`);
    expect(missingCredentialClose).toBe(1008);
    expect(await acceptedSockets(request)).toBe(connectionsBefore);
    console.log('R3 anonymous browser WebSocket: invalid and absent credential close locally with 1008; destination accepted connections +0');
    await meteredOnce(cells, created.id, 'forged internal headers', async () => {
      const response = await cells.post(home + database(created.id) + '/sql/execute', {
        headers: forgedHeaders, data: { statements: [{ sql: 'SELECT 1' }] },
      });
      await answer(response, 200, '200 forged internal marks do not affect forwarding, metering or deadline');
      expect(response.headers()['x-terrabase-region']).toBe('fra');
    });
    await meteredOnce(cells, created.id, 'REST', () => sql(cells, home, created.id, 'SELECT value FROM placement_rows ORDER BY value'));
    await meteredOnce(cells, created.id, 'regions', () => progress(cells, created.id, home));
    await page.setExtraHTTPHeaders({ Authorization: `Bearer ${grant.access_token}` });
    await meteredOnce(cells, created.id, 'WebSocket', () => headerRows(page, `${home.replace('https:', 'wss:')}${database(created.id)}/sql/ws`));
    await page.setExtraHTTPHeaders({});
    await meteredOnce(cells, created.id, 'Postgres', async () => expect(postgresRows(created.id)).toBe('1\n2\n3\n4'));

    const credential = readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[0];
    for (const socket of [false, true]) {
      const session = connect('http://127.0.0.1:8081');
      try {
        await once(session, 'remoteSettings');
        const stream = session.request({
          ':method': socket ? 'CONNECT' : 'GET',
          ':path': `${database(created.id)}/${socket ? 'sql/ws' : 'regions'}`,
          ':scheme': 'http',
          ':authority': 'gru.api.terrascale.tech',
          ...(socket ? { ':protocol': 'websocket' } : {}),
          authorization: `Bearer ${grant.access_token}`,
          'x-terrabase-peer-credential': credential,
          'x-terrabase-forwarded-for': '127.0.0.1',
        });
        const response = once(stream, 'response');
        stream.end();
        const [headers] = await response;
        let body = '';
        stream.setEncoding('utf8');
        for await (const chunk of stream) body += chunk;
        expect(headers[':status']).toBe(503);
        expect(headers['retry-after']).toBe('1');
        expect(JSON.parse(body).error.code).toBe('unavailable');
        console.log(`R3 forwarded ${socket ? 'header socket upgrade' : 'REST regions'} at nonholding gru: 503 unavailable, Retry-After 1, no second hop`);
      } finally {
        session.close();
      }
    }
    const peers = join(stateDir, 'peers');
    const original = readFileSync(peers, 'utf8');
    await meteredOnce(cells, created.id, 'no destination', async () => {
      writeFileSync(peers, original.replace(/^(journey-\d+ fra) \S+$/gm, '$1 127.0.0.9'));
      try {
        const missing = await cells.get(home + database(created.id) + '/regions');
        await answer(missing, 503, 'unavailable');
        expect(missing.headers()['retry-after']).toBe('1');
        const refused = await page.evaluate(({ url, token }) => new Promise<{ code: string; message: string }>((resolve, reject) => {
          const socket = new WebSocket(url, 'terrascale.sql.v1');
          socket.onopen = () => socket.send(JSON.stringify({ id: 1, type: 'auth', token }));
          socket.onerror = () => reject(new Error('unknown-writer auth-frame session failed'));
          socket.onmessage = message => {
            const answer = JSON.parse(String(message.data));
            socket.close();
            if (answer.type === 'error') resolve(answer.error);
            else reject(new Error('unknown writer unexpectedly accepted the session'));
          };
        }), { url: `${home.replace('https:', 'wss:')}${database(created.id)}/sql/ws`, token: grant.access_token });
        expect(refused).toMatchObject({ code: 'unavailable', message: 'the database region fra could not be reached' });
      } finally {
        writeFileSync(peers, original);
      }
      console.log('R3 unreachable writer: REST 503 unavailable and auth-frame socket unavailable');
    }, 'gru');
    await sql(cells, replica, created.id, 'CREATE TABLE relay_outcomes (value INTEGER)');
    await unanswered(cells, page, created.id, grant.access_token);
    const paused = Number(readFileSync(join(stateDir, 'pids', 'terrabase-fra.pid'), 'utf8').trim());
    process.kill(paused, 'SIGSTOP');
    try {
      const started = performance.now();
      const response = await cells.post(`${home}${database(created.id)}/sql/execute`, {
        data: { statements: [{ sql: 'SELECT value FROM placement_rows ORDER BY value' }] },
      });
      const elapsed = performance.now() - started;
      await answer(response, 503, 'write_outcome_unknown');
      expect(elapsed).toBeGreaterThan(4000);
      expect(elapsed).toBeLessThan(10_000);
      console.log(`R3 connected destination never answers: cut at ${Math.round(elapsed)} ms, 503 write_outcome_unknown; no replay`);
    } finally {
      process.kill(paused, 'SIGCONT');
    }
    const rule = ['OUTPUT', '-p', 'tcp', '-d', '127.0.0.9', '--dport', '8081', '-j', 'DROP'];
    const firewall = { env: { ...process.env, XTABLES_LOCKFILE: join(stateDir, 'iptables.lock') } };
    execFileSync('iptables', ['-A', ...rule], firewall);
    try {
      writeFileSync(peers, original.replace(/^(journey-\d+ fra) \S+$/gm, '$1 127.0.0.9'));
      const started = performance.now();
      const response = await cells.post(`${home}${database(created.id)}/sql/execute`, {
        data: { statements: [{ sql: 'SELECT value FROM placement_rows ORDER BY value' }] },
      });
      const elapsed = performance.now() - started;
      await answer(response, 503, 'unavailable');
      expect(response.headers()['retry-after']).toBe('1');
      expect(await response.text()).not.toContain('127.0.0.9');
      expect(elapsed).toBeGreaterThan(4000);
      expect(elapsed).toBeLessThan(10_000);
      console.log(`R3 unreachable REST: 503 unavailable, Retry-After 1, ${Math.round(elapsed)} ms, no internal host`);
    } finally {
      writeFileSync(peers, original);
      execFileSync('iptables', ['-D', ...rule], firewall);
    }
    const transactionGrant = await json<{ access_token: string }>(await request.post(`${auth}/terrascale/oauth/token`, {
      form: {
        grant_type: 'client_credentials', client_id: current.apiKey.slice(0, 26), client_secret: current.apiKey,
        project: current.projectId, scope: 'sql:read sql:write databases:read',
      },
    }));
    saveSecret('placement-transaction-token', transactionGrant.access_token);
    await page.evaluate(({ url, token }) => new Promise<void>((resolve, reject) => {
      const socket = new WebSocket(url, 'terrascale.sql.v1');
      const state = window as Window & { proxiedTransaction?: WebSocket; proxyClosed?: Promise<number> };
      state.proxiedTransaction = socket;
      state.proxyClosed = new Promise<number>(closed => { socket.onclose = event => closed(event.code); });
      socket.onopen = () => socket.send(JSON.stringify({ id: 1, type: 'auth', token }));
      socket.onmessage = message => {
        const answer = JSON.parse(String(message.data));
        if (answer.type === 'error') reject(new Error(JSON.stringify(answer.error)));
        else resolve();
      };
      socket.onerror = () => reject(new Error('the transaction proxy could not open'));
    }), { url: `${home.replace('https:', 'wss:')}${database(created.id)}/sql/ws`, token: transactionGrant.access_token });
    await answer(await cells.patch(`${replica}${database(created.id)}`, { data: { regions: ['fra', 'gru'] } }), 200, '200 copy rejoined');
    await replicated(cells, home, created.id, 4);
    const rejoinedGrant = await json<{ access_token: string }>(await request.post(`${auth}/terrascale/oauth/token`, {
      form: {
        grant_type: 'client_credentials',
        client_id: current.apiKey.slice(0, 26),
        client_secret: current.apiKey,
        project: current.projectId,
        scope: 'sql:read sql:write databases:read',
      },
    }));
    saveSecret('placement-rejoined-socket-token', rejoinedGrant.access_token);
    await page.setExtraHTTPHeaders({ Authorization: `Bearer ${rejoinedGrant.access_token}`, 'X-TerraScale-Region-Pin': 'gru' });
    expect(await headerRows(page, `${home.replace('https:', 'wss:')}${database(created.id)}/sql/ws`)).toEqual([[1], [2], [3], [4]]);
    expect(postgresRows(created.id)).toBe('1\n2\n3\n4');
    console.log('R3 rejoined copy: new header-authenticated WebSocket and Postgres sessions read rows 1,2,3,4 despite the historical writer fence');
    const transaction = await page.evaluate(() => new Promise<boolean>((resolve, reject) => {
      const socket = (window as Window & { proxiedTransaction?: WebSocket }).proxiedTransaction!;
      socket.onmessage = message => {
        const answer = JSON.parse(String(message.data));
        if (answer.type === 'error') reject(new Error(JSON.stringify(answer.error)));
        else if (answer.id === 2) socket.send(JSON.stringify({ id: 3, type: 'execute', statements: [{ sql: 'INSERT INTO placement_rows VALUES (987654)' }] }));
        else if (answer.id === 3) resolve(answer.transaction);
      };
      socket.send(JSON.stringify({ id: 2, type: 'begin' }));
    }));
    expect(transaction, 'the proxied session has acknowledged an uncommitted write in an open transaction').toBe(true);
    await expect.poll(async () => {
      const response = await cells.patch(`${replica}${database(created.id)}`, { data: { regions: ['gru'] } });
      if (response.status() === 503) {
        await answer(response, 503, 'replica_not_ready');
        return false;
      }
      await answer(response, 200, '200 writer returned to gru');
      return true;
    }, { message: 'the writer moves while the proxied transaction is open', timeout: 60_000, intervals: [1000] }).toBe(true);
    expect(await page.evaluate(() => (window as Window & { proxyClosed?: Promise<number> }).proxyClosed)).toBe(1012);
    expect((await sql(cells, home, created.id, 'SELECT value FROM placement_rows ORDER BY value')).results[0].rows).toEqual([[1], [2], [3], [4]]);
    console.log('R3 open transaction through proxy: acknowledged uncommitted insert, writer move, close 1012; value 987654 absent');

  } catch (error) {
    console.log('R3 primary failure:', error);
    throw error;
  } finally {
    if (pg && pg.exitCode === null) {
      pg.kill('SIGTERM');
      await pgExited;
    }
    await page.setExtraHTTPHeaders({});
    await removeDatabase(cells, created.id);
  }
});

test('R4 replace the only region, retain additions after the first refusal', async ({ cells }) => {
  const created = await create(cells, ['gru']);
  const statuses: number[] = [];
  try {
    await seed(cells, created.id);
    const first = await patch(cells, created.id, ['fra']);
    statuses.push(first.status());
    await answer(first, 503, 'replica_not_ready');
    expect((await progress(cells, created.id)).regions.map(row => row.region)).toEqual(['fra', 'gru']);
    await move(cells, created.id, 'R4', 90_000, statuses);
    expect((await sql(cells, replica, created.id, 'SELECT value FROM placement_rows ORDER BY value')).results[0].rows).toEqual([[1], [2], [3]]);
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('R5 replace the writer under twenty acknowledged commits per second', async ({ cells }) => {
  test.setTimeout(180_000);
  const created = await create(cells, ['gru', 'fra']);
  const acknowledged: number[] = [];
  const lags: number[] = [];
  const statuses: number[] = [];
  const backgroundErrors: unknown[] = [];
  let stop = false;
  let writer: Promise<void> | undefined;
  let sampling: Promise<void> | undefined;
  try {
    await sql(cells, home, created.id, 'CREATE TABLE placement_rows (value INTEGER PRIMARY KEY)');
    await observed(cells, created.id);
    const deadline = Date.now() + 120_000;
    writer = (async () => {
      for (let id = 1; !stop && Date.now() < deadline;) {
        const next = Date.now() + 50;
        const response = await cells.post(`${home}${database(created.id)}/sql/execute`, { data: { statements: [{ sql: `INSERT INTO placement_rows VALUES (${id})` }] } });
        if (statuses.length > 0 || response.status() !== 200) statuses.push(response.status());
        if (response.status() === 200) {
          acknowledged.push(id++);
        } else if (response.status() === 409) {
          await answer(response, 409, 'serialization_conflict');
        } else if (response.status() === 503) {
          await answer(response, 503, 'unavailable');
          expect(response.headers()['retry-after']).toBe('1');
        } else {
          await answer(response, 200, '200 write through the former writer');
        }
        await delay(Math.max(0, next - Date.now()));
      }
    })().catch(error => { backgroundErrors.push(error); });
    await expect.poll(() => acknowledged.length, { message: 'writer commits before the move', timeout: 10_000, intervals: [50] }).toBeGreaterThanOrEqual(20);
    const sample = async () => {
      const row = (await progress(cells, created.id)).regions.find(row => row.region === 'fra');
      if (row?.lagMs !== null && row?.lagMs !== undefined) lags.push(row.lagMs);
    };
    await sample();
    sampling = (async () => {
      while (!stop && Date.now() < deadline) {
        await sample();
        await delay(1000);
      }
    })().catch(error => { backgroundErrors.push(error); });
    await move(cells, created.id, 'R5', Math.max(1, deadline - Date.now()));
    stop = true;
    await Promise.all([writer, sampling]);
    const result = await sql(cells, replica, created.id, 'SELECT value FROM placement_rows ORDER BY value');
    const found = result.results[0].rows.map(row => row[0]);
    console.log(`R5 writer statuses: ${JSON.stringify(statuses)}; acknowledged: ${acknowledged.length}; found: ${found.length}`);
    if (JSON.stringify(found) !== JSON.stringify(acknowledged)) console.log(`R5 database: ${created.id}; acknowledged ids: ${JSON.stringify(acknowledged)}; found ids: ${JSON.stringify(found)}`);
    expect(found).toEqual(acknowledged);
    expect(new Set(found).size).toBe(found.length);
    expect(lags.length, 'fra lag samples').toBeGreaterThan(0);
    if (backgroundErrors.length > 0) throw new AggregateError(backgroundErrors, 'R5 writer or lag sampling failed');
    await sql(cells, home, created.id, 'INSERT INTO placement_rows VALUES (0)');
    expect((await sql(cells, replica, created.id, 'SELECT value FROM placement_rows WHERE value = 0')).results[0].rows).toEqual([[0]]);
  } finally {
    stop = true;
    await Promise.all([writer, sampling]);
    lags.sort((a, b) => a - b);
    console.log(`R5 lagMs min/median/max: ${lags[0]}/${lags[Math.floor(lags.length / 2)]}/${lags.at(-1)}; samples: ${lags.length}`);
    await removeDatabase(cells, created.id);
  }
});

test('R6 repeat and conflicting placements during a move', async ({ cells }) => {
  test.setTimeout(20 * 60_000 + 30_000);
  let caught = false;
  for (let attempt = 1; attempt <= 20 && !caught; attempt++) {
    const created = await create(cells, ['gru', 'fra']);
    try {
      await seed(cells, created.id);
      await observed(cells, created.id);
      const moving = patch(cells, created.id, ['fra']);
      const concurrent = [
        await patch(cells, created.id, ['fra']),
        await patch(cells, created.id, ['gru', 'fra']),
      ];
      const initial = await moving;
      for (const response of [initial, ...concurrent]) {
        expect([200, 503, 409], '200 updated, 503 replica_not_ready or 409 move_in_progress').toContain(response.status());
        if (response.status() === 503) await answer(response, 503, 'replica_not_ready');
        if (response.status() === 409) await answer(response, 409, 'move_in_progress');
      }
      caught = concurrent[0].status() === 503 && concurrent[1].status() === 409;
      console.log(`R6 attempt ${attempt}: ${[initial, ...concurrent].map(response => response.status()).join(',')}`);
      await move(cells, created.id, 'R6 cleanup move', 60_000);
    } finally {
      await removeDatabase(cells, created.id);
    }
  }
  console.log(`R6: ${caught ? 'PASS' : 'not observed after 20 attempts'}`);
});

test('R7 unchanged placement and the documented nine-member maximum', async ({ cells }) => {
  const created = await create(cells, ['gru', 'fra']);
  try {
    const before = await json<Record<string, unknown>>(await cells.get(`${home}${database(created.id)}`));
    const repeated = await patch(cells, created.id, ['fra', 'gru']);
    await answer(repeated, 200, '200 unchanged database');
    expect(await repeated.json()).toEqual(before);
    expect(before.placementVersion, 'placementVersion is exposed').toEqual(expect.any(Number));
    const after = await json<Record<string, unknown>>(await cells.get(`${home}${database(created.id)}`));
    expect(after.placementVersion, 'unchanged request retains placementVersion').toBe(before.placementVersion);
    console.log(`R7 placementVersion unchanged: ${before.placementVersion}`);
    const over = await patch(cells, created.id, ['gru', 'fra', 'sjc', 'iad', 'lhr', 'cdg', 'sin', 'syd', 'nrt', 'bom']);
    await answer(over, 400, 'invalid_request');
    expect((await over.json()).error.message).toBe('regions can place a database in at most 9 regions, including the writer and any intermediate placement');
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('R8 a TTL branch keeps its identity through catalog sync, a move and a rename', async ({ browser, playwright, request }, info) => {
  const context = await auditedContext(browser, {});
  try {
    const page = await context.newPage();
    const audit = new Audit(page, info);
    const email = await newAddress(name('identity'));
    const password = randomBytes(24).toString('base64url');
    saveSecret('identity-password', password);
    await signUp(page, audit, email, password, 'database identity owner');
    await verify(page, email);
    await onboard(page, audit, 'Database identity owner', name('identity-project'), 'database identity owner');
    const projectId = new URL(page.url()).searchParams.get('projectId') ?? '';
    expect(projectId).not.toBe('');
    await page.goto(`${dash}/organization?tab=billing`);
    await page.locator('article.ts-plan', { has: page.locator('#plan-pro') }).getByRole('button', { name: 'Upgrade to Pro', exact: true }).click();
    await page.waitForURL(url => url.origin === 'https://checkout.stripe.com' && url.pathname.startsWith('/c/pay/cs_test_'));
    await page.getByRole('button', { name: 'Subscribe', exact: true }).click();
    await page.waitForURL(url => url.origin === dash && url.pathname === '/organization' && url.searchParams.get('payment') === 'success');
    await expect(page.locator('#billing-summary .ts-billing-plan-name')).toHaveText('Pro');
    await audit.check('placement branch owner upgraded');
    const key = await createApiKey(page, audit, name('identity-key'), 'database identity');
    const cells = await authenticatedCells(playwright.request, request, key, projectId);
    try {
      const source = await create(cells, ['gru'], projectId);
      let branch: Database | undefined;
      try {
        await seed(cells, source.id, projectId);
        branch = await json<Database>(await cells.post(`${home}${database(source.id, projectId)}/branches`, {
          data: { name: name('identity'), ttlSeconds: 3600 },
        }), 201);
        const id = branch.id;
        const original = await json<Record<string, unknown>>(await cells.get(`${home}${database(id, projectId)}`));
        expect(original.branchedFrom).toBe(source.id);
        expect(original.createdAt).toEqual(expect.any(String));
        expect(original.expiresAt).toEqual(expect.any(String));
        await answer(await patch(cells, id, ['gru', 'fra'], projectId), 200, '200 database updated with writer retained');
        await observed(cells, id, projectId);
        await replicated(cells, replica, id, 3, projectId);

        const renamed = await json<Record<string, unknown>>(await cells.patch(`${home}${database(id, projectId)}`, { data: { name: name('before-move') } }));
        const before = await json<Record<string, unknown>>(await cells.put(`${home}${database(id, projectId)}/ttl`, { data: { ttlSeconds: 7200 } }));
        expect(before.name).toBe(renamed.name);
        expect(before.expiresAt).not.toBe(original.expiresAt);
        expect(before.placementVersion).toBe(renamed.placementVersion);
        const facts = ({ region, homeRegion, replicaRegions, placementVersion, allRegions, regions, ...identity }: Record<string, unknown>) => identity;
        expect(facts(before)).toEqual({ ...facts(original), name: before.name, expiresAt: before.expiresAt });
        await expect.poll(async () => facts(await json<Record<string, unknown>>(await cells.get(`${replica}${database(id, projectId)}`))),
          { message: 'the existing replica receives name and expiry changes without a placement change', timeout: 45_000, intervals: [250, 500, 1000] })
          .toEqual(facts(before));

        const cleared = await json<Record<string, unknown>>(await cells.delete(`${home}${database(id, projectId)}/ttl`));
        expect(cleared.expiresAt).toBeNull();
        await expect.poll(async () => facts(await json<Record<string, unknown>>(await cells.get(`${replica}${database(id, projectId)}`))),
          { message: 'the existing follower receives a cleared TTL from its home', timeout: 45_000, intervals: [250, 500, 1000] })
          .toEqual(facts(cleared));

        const atPromotion = await json<Record<string, unknown>>(await cells.put(`${home}${database(id, projectId)}/ttl`, { data: { ttlSeconds: 10800 } }));
        expect(atPromotion.expiresAt).not.toBe(before.expiresAt);
        await move(cells, id, 'R8', 60_000, [], projectId);
        const moved = await json<Record<string, unknown>>(await cells.get(`${replica}${database(id, projectId)}`));
        expect(facts(moved)).toEqual(facts(atPromotion));
        const changedName = name('after-move');
        const renamedAfterMove = await json<Record<string, unknown>>(await cells.patch(`${replica}${database(id, projectId)}`, { data: { name: changedName } }));
        expect(renamedAfterMove).toEqual({ ...moved, name: changedName });
        expect(await json(await cells.get(`${replica}${database(id, projectId)}`))).toEqual(renamedAfterMove);
        console.log('R8 follower received a changed TTL, then a cleared TTL; promotion retained the latest expiry and immutable identity; rename changes only name');
      } finally {
        if (branch) await removeDatabase(cells, branch.id, projectId);
        await removeDatabase(cells, source.id, projectId);
      }
    } finally {
      await cells.dispose();
    }

    await page.goto(`${dash}/profile?tab=danger`);
    await page.locator('main').getByRole('button', { name: 'Delete account', exact: true }).click();
    const dialog = page.locator('#delete-account');
    await dialog.locator('#delete-account-email').fill(email);
    await dialog.getByRole('button', { name: 'Delete account', exact: true }).click();
    await confirmWithPassword(page, audit, password, 'database identity owner deletion');
    await page.waitForURL(url => url.origin === dash && url.pathname === '/account-deleted');
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Account deleted');
    await audit.check('database identity owner deleted');
    await audit.finish();
  } finally {
    await saveCookies(context);
    await context.close();
  }
});

const multiple = test.extend<{ owner: { cells: Cells; projectId: string; organizationId: string; page: Page; audit: Audit } }>({
  owner: async ({ browser, playwright, request }, use, info) => {
    const context = await auditedContext(browser, {});
    try {
      const page = await context.newPage();
      const audit = new Audit(page, info);
      const email = await newAddress(name('catalog'));
      const password = randomBytes(24).toString('base64url');
      saveSecret('catalog-password', password);
      await signUp(page, audit, email, password, 'catalog owner');
      await verify(page, email);
      await onboard(page, audit, 'Catalog owner', 'C35 catalog project', 'catalog owner');
      const projectId = new URL(page.url()).searchParams.get('projectId') ?? '';
      const organizationId = new URL(page.url()).searchParams.get('organizationId') ?? '';
      expect(projectId).not.toBe('');
      expect(organizationId).not.toBe('');
      await page.goto(`${dash}/organization?tab=billing`);
      await page.locator('article.ts-plan', { has: page.locator('#plan-pro') }).getByRole('button', { name: 'Upgrade to Pro', exact: true }).click();
      await page.waitForURL(url => url.origin === 'https://checkout.stripe.com' && url.pathname.startsWith('/c/pay/cs_test_'));
      await page.getByRole('button', { name: 'Subscribe', exact: true }).click();
      await page.waitForURL(url => url.origin === dash && url.pathname === '/organization' && url.searchParams.get('payment') === 'success');
      await expect(page.locator('#billing-summary .ts-billing-plan-name')).toHaveText('Pro');
      await audit.check('catalog owner upgraded');
      const key = await createApiKey(page, audit, name('catalog-key'), 'catalog owner');
      const cells = await authenticatedCells(playwright.request, request, key, projectId);
      try {
        await use({ cells, projectId, organizationId, page, audit });
      } finally {
        await cells.dispose();
      }
      await page.goto(`${dash}/profile?tab=danger`);
      await page.locator('main').getByRole('button', { name: 'Delete account', exact: true }).click();
      const dialog = page.locator('#delete-account');
      await dialog.locator('#delete-account-email').fill(email);
      await dialog.getByRole('button', { name: 'Delete account', exact: true }).click();
      await confirmWithPassword(page, audit, password, 'catalog owner deletion');
      await page.waitForURL(url => url.origin === dash && url.pathname === '/account-deleted');
      await audit.check('catalog owner deleted');
      await audit.finish();
    } finally {
      await saveCookies(context);
      await context.close();
    }
  },
});

async function named(cells: Cells, region: string, label: string, regions: string[], projectId: string): Promise<Database> {
  return json<Database>(await cells.post(`${region}${project(projectId)}/databases`, { data: { name: label, regions } }), 201);
}

async function rename(cells: Cells, region: string, id: string, label: string, projectId: string): Promise<void> {
  await answer(await cells.patch(`${region}${database(id, projectId)}`, { data: { name: label } }), 200, '200 database renamed');
}

async function recordedName(cells: Cells, id: string, projectId: string): Promise<string> {
  return (await json<{ name: string }>(await cells.get(`${replica}${database(id, projectId)}`))).name;
}

async function catalogRecord<T>(cell: string, path: string, status = 200): Promise<T> {
  const credential = readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[0];
  const session = connect(cell);
  const stream = session.request({ ':method': 'GET', ':path': path, 'x-terrabase-peer-credential': credential });
  session.on('error', error => stream.destroy(error));
  try {
    const response = once(stream, 'response');
    stream.end();
    const [headers] = await response;
    let body = '';
    stream.setEncoding('utf8');
    for await (const chunk of stream) body += chunk;
    expect(Number(headers[':status']), path).toBe(status);
    return JSON.parse(body) as T;
  } finally {
    session.close();
  }
}

async function convergedNames(cells: Cells, ids: string[], real: string[], expected: string[], projectId: string): Promise<void> {
  const seen: string[] = [];
  await expect.poll(async () => {
    const names = await Promise.all(ids.map(id => recordedName(cells, id, projectId)));
    seen.push(...names);
    return names;
  }, { message: 'fra applies the chain within two 30-second sweeps', timeout: 65_000, intervals: [200] }).toEqual(expected);
  for (const value of seen) expect(real, 'every management GET throughout convergence returned a real name').toContain(value);
  console.log('management GET every 200 ms: ' + seen.length / ids.length + ' samples per database, only real names');
}

multiple('R9 a taken follower name does not stop later catalog entries or pulls', async ({ owner: { cells, projectId } }) => {
  const original = name('original');
  const occupied = name('occupied');
  const x = await named(cells, home, original, ['gru', 'fra'], projectId);
  const y = await named(cells, replica, occupied, ['fra'], projectId);
  let later: Database | undefined;
  try {
    await seed(cells, x.id, projectId);
    await observed(cells, x.id, projectId);
    await rename(cells, home, x.id, occupied, projectId);
    later = await create(cells, ['gru'], projectId);
    await seed(cells, later.id, projectId);
    await answer(await patch(cells, later.id, ['gru', 'fra'], projectId), 200, '200 later database starts following');
    await observed(cells, later.id, projectId);
    await replicated(cells, replica, later.id, 3, projectId);
    expect(await recordedName(cells, x.id, projectId)).toBe(original);
    expect(await recordedName(cells, y.id, projectId)).toBe(occupied);
    console.log('R9 fra kept the conflicting name and completed a pull for a later gru database');
  } finally {
    if (later) await removeDatabase(cells, later.id, projectId);
    await removeDatabase(cells, x.id, projectId);
    await removeDatabase(cells, y.id, projectId);
  }
});

multiple('R10 a home name swap converges on the follower within two sweeps', async ({ owner: { cells, projectId } }) => {
  const a = name('swap-a');
  const b = name('swap-b');
  const x = await named(cells, home, a, ['gru', 'fra'], projectId);
  const y = await named(cells, home, b, ['gru', 'fra'], projectId);
  try {
    await seed(cells, x.id, projectId);
    await seed(cells, y.id, projectId);
    await observed(cells, x.id, projectId);
    await observed(cells, y.id, projectId);
    expect(await recordedName(cells, x.id, projectId)).toBe(a);
    expect(await recordedName(cells, y.id, projectId)).toBe(b);
    const started = performance.now();
    const temporary = name('swap-tmp');
    await rename(cells, home, x.id, temporary, projectId);
    await rename(cells, home, y.id, a, projectId);
    await rename(cells, home, x.id, b, projectId);
    expect(performance.now() - started, 'all three renames complete in less than one sweep').toBeLessThan(30_000);
    const intermediate = await Promise.all([x.id, y.id].map(id => recordedName(cells, id, projectId)));
    console.log('R10 path: ' + (intermediate.join() === [a, b].join() ? 'missed intermediate' : 'sweep observed intermediate or final names'));
    await convergedNames(cells, [x.id, y.id], [a, b, temporary], [b, a], projectId);
    console.log('R10 fra converged to both swapped names within two catalog sweeps');
  } finally {
    await removeDatabase(cells, x.id, projectId);
    await removeDatabase(cells, y.id, projectId);
  }
});

multiple('R11 a conflicting promotion is refused before takeover and succeeds after a rename', async ({ owner: { cells, projectId, page, audit } }, info) => {
  const occupied = 'c35-move-name';
  const original = 'c35-move-original';
  const x = await named(cells, home, original, ['gru', 'fra'], projectId);
  const y = await named(cells, replica, occupied, ['fra'], projectId);
  try {
    await seed(cells, x.id, projectId);
    await observed(cells, x.id, projectId);
    await page.goto(`${dash}/databases/${x.id}/replication`);
    await expect(page.locator('#database-regions input[name="regions"][value="gru" i]')).toBeChecked();
    await rename(cells, home, x.id, occupied, projectId);
    const before = await json<Record<string, unknown>>(await cells.get(`${home}${database(x.id, projectId)}`));
    await answer(await patch(cells, x.id, ['fra'], projectId), 409, 'name_taken');
    await page.goto(`${dash}/databases/${x.id}/replication`);
    await page.locator('#database-regions input[name="regions"][value="gru" i]').uncheck();
    audit.expectStatus(/\/api\/v1\/dashboard\/databases\/placement(?:\?|$)/, 409);
    const refused = page.waitForResponse(response => response.request().method() === 'POST'
      && new URL(response.url()).pathname === '/api/v1/dashboard/databases/placement');
    await page.getByRole('button', { name: 'Save regions', exact: true }).click();
    expect((await refused).status()).toBe(409);
    await expect(page.locator('main .ts-alert-danger .ts-alert-body')).toHaveText('Another database in fra already uses this name. Rename one of them, then save again.');
    await audit.check('name conflict explains how to save regions');
    for (const viewport of [{ width: 1280, height: 800 }, { width: 390, height: 844 }]) {
      await page.setViewportSize(viewport);
      await page.screenshot({ path: info.outputPath(`regions-name-taken-dark-${viewport.width}.png`), fullPage: true });
    }
    const after = await json<Record<string, unknown>>(await cells.get(`${home}${database(x.id, projectId)}`));
    expect(after).toEqual(before);
    expect(await recordedName(cells, x.id, projectId)).toBe(original);
    expect((await progress(cells, x.id, home, projectId)).regions.filter(row => row.role === 'writer').map(row => row.region)).toEqual(['gru']);
    await sql(cells, home, x.id, 'INSERT INTO placement_rows VALUES (4)', projectId);
    await rename(cells, replica, y.id, name('move-freed'), projectId);
    await move(cells, x.id, 'R11', 60_000, [], projectId);
    expect((await sql(cells, replica, x.id, 'SELECT value FROM placement_rows ORDER BY value', projectId)).results[0].rows).toEqual([[1], [2], [3], [4]]);
    expect(await recordedName(cells, x.id, projectId)).toBe(occupied);
    console.log('R11 409 name_taken left gru writing; renaming the conflicting database allowed the move');
  } finally {
    await removeDatabase(cells, x.id, projectId);
    await removeDatabase(cells, y.id, projectId);
  }
});


multiple('R12 a missed three-way home rotation converges without exposing placeholders', async ({ owner: { cells, projectId } }) => {
  const a = name('rotate-a');
  const b = name('rotate-b');
  const c = name('rotate-c');
  const x = await named(cells, home, a, ['gru', 'fra'], projectId);
  const y = await named(cells, home, b, ['gru', 'fra'], projectId);
  const z = await named(cells, home, c, ['gru', 'fra'], projectId);
  try {
    for (const row of [x, y, z]) {
      await seed(cells, row.id, projectId);
      await observed(cells, row.id, projectId);
    }
    expect(await Promise.all([x.id, y.id, z.id].map(id => recordedName(cells, id, projectId)))).toEqual([a, b, c]);
    const started = performance.now();
    const temporary = name('rotate-tmp');
    await rename(cells, home, x.id, temporary, projectId);
    await rename(cells, home, z.id, a, projectId);
    await rename(cells, home, y.id, c, projectId);
    await rename(cells, home, x.id, b, projectId);
    expect(performance.now() - started, 'the rotation finishes in less than one sweep').toBeLessThan(30_000);
    const intermediate = await Promise.all([x.id, y.id, z.id].map(id => recordedName(cells, id, projectId)));
    console.log('R12 path: ' + (intermediate.join() === [a, b, c].join() ? 'missed intermediate' : 'sweep observed intermediate or final names'));
    await convergedNames(cells, [x.id, y.id, z.id], [a, b, c, temporary], [b, c, a], projectId);
    console.log('R12 fra converged to the three-way rotation within two sweeps; no GET exposed a placeholder');
  } finally {
    for (const row of [x, y, z]) await removeDatabase(cells, row.id, projectId);
  }
});

multiple('R13 promotion resolves a same-home name swap through placement before takeover', async ({ owner: { cells, projectId } }) => {
  const a = name('promote-a');
  const b = name('promote-b');
  const x = await named(cells, home, a, ['gru', 'fra'], projectId);
  const y = await named(cells, home, b, ['gru', 'fra'], projectId);
  try {
    for (const row of [x, y]) {
      await seed(cells, row.id, projectId);
      await observed(cells, row.id, projectId);
    }
    await rename(cells, home, x.id, name('promote-tmp'), projectId);
    await rename(cells, home, y.id, a, projectId);
    await rename(cells, home, x.id, b, projectId);
    const intermediate = await Promise.all([x.id, y.id].map(id => recordedName(cells, id, projectId)));
    console.log('R13 path: ' + (intermediate.join() === [a, b].join() ? 'promotion resolves unswept swap' : 'sweep already observed swap'));
    await answer(await patch(cells, x.id, ['fra'], projectId), 200, '200 promotion resolves the stale same-home holder');
    expect(await Promise.all([x.id, y.id].map(id => recordedName(cells, id, projectId)))).toEqual([b, a]);
    expect((await progress(cells, x.id, replica, projectId)).regions.filter(row => row.role === 'writer').map(row => row.region)).toEqual(['fra']);
    console.log('R13 same-home promotion resolved both names through the existing placement request before takeover');
  } finally {
    await removeDatabase(cells, x.id, projectId);
    await removeDatabase(cells, y.id, projectId);
  }
});

multiple('R14 a sweep resolves a same-home holder excluded from its listing through placement', async ({ owner: { cells, projectId, organizationId } }) => {
  const a = name('lookup-a');
  const b = name('lookup-b');
  const freed = name('lookup-freed');
  const x = await named(cells, home, a, ['gru', 'fra'], projectId);
  const y = await named(cells, home, b, ['gru', 'fra'], projectId);
  const holder = () => catalogRecord<{ name: string; homeRegion: string; replicaRegions: string[] }>('http://127.0.0.2:8081',
    `/v1/sync/organizations/${organizationId}/projects/${projectId}/databases/${y.id}/placement`);
  try {
    for (const row of [x, y]) {
      await seed(cells, row.id, projectId);
      await observed(cells, row.id, projectId);
    }
    expect(await recordedName(cells, y.id, projectId)).toBe(b);
    await answer(await patch(cells, y.id, ['gru'], projectId), 200, '200 holder excluded from fra listing');
    await expect.poll(async () => (await holder()).replicaRegions,
      { message: 'fra has recorded the holder removal before its home renames it', timeout: 45_000, intervals: [250, 500, 1000] })
      .toEqual([]);
    expect(await holder()).toMatchObject({ name: b, homeRegion: 'gru' });
    let after: string | null = null;
    do {
      const listing: { databases: { databaseId: string }[]; next: string | null } = await catalogRecord('http://127.0.0.1:8081',
        '/v1/sync/regions/fra/databases' + (after ? `?after=${encodeURIComponent(after)}` : ''));
      expect(listing.databases.map(row => row.databaseId), 'the home listing excludes the holder').not.toContain(y.id);
      after = listing.next;
    } while (after);
    await rename(cells, home, y.id, freed, projectId);
    await rename(cells, home, x.id, b, projectId);
    await convergedNames(cells, [x.id], [a, b], [b], projectId);
    expect((await holder()).name).toBe(freed);
    expect((await json<Database>(await cells.get(`${home}${database(y.id, projectId)}`))).regions).toEqual(['gru']);
    console.log('R14 holder removed from fra membership/listing, retained in fra catalog; sweep used home placement lookup without promotion');
  } finally {
    for (const row of [x, y]) await removeDatabase(cells, row.id, projectId);
  }
});

multiple('R15 an observed intermediate home rename still converges after a swap', async ({ owner: { cells, projectId } }) => {
  const a = name('intermediate-a');
  const b = name('intermediate-b');
  const temporary = name('intermediate-tmp');
  const x = await named(cells, home, a, ['gru', 'fra'], projectId);
  const y = await named(cells, home, b, ['gru', 'fra'], projectId);
  try {
    for (const row of [x, y]) {
      await seed(cells, row.id, projectId);
      await observed(cells, row.id, projectId);
    }
    await rename(cells, home, x.id, temporary, projectId);
    await expect.poll(() => recordedName(cells, x.id, projectId),
      { message: 'the follower observes the intermediate name before the swap finishes', timeout: 45_000, intervals: [250, 500, 1000] })
      .toBe(temporary);
    await rename(cells, home, y.id, a, projectId);
    await rename(cells, home, x.id, b, projectId);
    await convergedNames(cells, [x.id, y.id], [a, b, temporary], [b, a], projectId);
    console.log('R15 reproduced the intermediate-name precondition that broke the old assertion; both final names converged');
  } finally {
    for (const row of [x, y]) await removeDatabase(cells, row.id, projectId);
  }
});

test('R16 a removed region proxies requests, erases its copy and takes the database back', async ({ cells }) => {
  const created = await create(cells, ['gru', 'fra']);
  const execute = (region: string, statement: string) => cells.post(`${region}${database(created.id)}/sql/execute`, { data: { statements: [{ sql: statement }] } });
  const rows = async (region: string) => (await sql(cells, region, created.id, 'SELECT value FROM placement_rows ORDER BY value')).results[0].rows;
  try {
    await seed(cells, created.id);
    await observed(cells, created.id);
    await replicated(cells, replica, created.id, 3);
    expect(files('terrabase', created.id), 'gru holds the database before the removal').not.toEqual([]);
    await move(cells, created.id, 'R16', 60_000);
    await sql(cells, replica, created.id, 'INSERT INTO placement_rows VALUES (4)');
    const renamed = name('regions-renamed');
    await answer(await cells.patch(`${replica}${database(created.id)}`, { data: { name: renamed } }), 200, '200 renamed through fra');

    await expect.poll(async () => {
      const response = await execute(home, 'SELECT value FROM placement_rows ORDER BY value');
      await answer(response, 200, '200 proxied read through gru');
      return response.headers()['x-terrabase-region'];
    }, { message: 'gru proxies to the retained region', timeout: 45_000, intervals: [250, 500, 1000] }).toBe('fra');
    expect(await json(await cells.get(`${home}${database(created.id)}`))).toMatchObject({ id: created.id, name: renamed, regions: ['fra'] });
    expect((await progress(cells, created.id)).regions.map(row => row.region)).toEqual(['fra']);
    expect(await rows(home)).toEqual([[1], [2], [3], [4]]);
    await answer(await execute(home, 'INSERT INTO placement_rows VALUES (99)'), 200, '200 proxied write through gru');
    const listed = await json<{ items: { id: string; name: string; regions: string[] }[] }>(await cells.get(`${home}${project()}/databases`));
    expect(listed.items.filter(item => item.id === created.id), 'the listing through gru shows the writer\'s record').toMatchObject([{ name: renamed, regions: ['fra'] }]);
    await expect.poll(() => files('terrabase', created.id),
      { message: 'gru erased its copy of the database', timeout: 45_000, intervals: [250, 500, 1000] }).toEqual([]);

    expect(await json(await cells.get(`${replica}${database(created.id)}`))).toMatchObject({ id: created.id, name: renamed, regions: ['fra'] });
    expect((await progress(cells, created.id, replica)).regions.map(row => row.region)).toEqual(['fra']);
    await sql(cells, replica, created.id, 'INSERT INTO placement_rows VALUES (5)');
    expect(await rows(replica)).toEqual([[1], [2], [3], [4], [5], [99]]);

    const added = await patch(cells, created.id, ['fra', 'gru']);
    await answer(added, 200, '200 database updated with writer retained');
    expect((await added.json()).regions).toEqual(['fra', 'gru']);
    await replicated(cells, home, created.id, 5);
    expect(await rows(home)).toEqual([[1], [2], [3], [4], [5], [99]]);
    expect(files('terrabase', created.id), 'gru holds a copy again').not.toEqual([]);
    expect(await json(await cells.get(`${home}${database(created.id)}`))).toMatchObject({ id: created.id, regions: ['fra', 'gru'] });
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('R17 a removed follower retains routing and rejoins with the current name and data', async ({ cells }) => {
  const created = await create(cells, ['gru', 'fra']);
  const renamed = name('removed-follower');
  try {
    await seed(cells, created.id);
    await observed(cells, created.id);
    await replicated(cells, replica, created.id, 3);
    await answer(await patch(cells, created.id, ['gru']), 200, '200 follower removed');
    await sql(cells, home, created.id, 'INSERT INTO placement_rows VALUES (4)');
    await expect.poll(async () => (await json<{ regions: string[] }>(await cells.get(`${replica}${database(created.id)}`))).regions,
      { message: 'the removed follower proxies to the retained region', timeout: 45_000, intervals: [250, 500, 1000] }).toEqual(['gru']);
    await rename(cells, home, created.id, renamed, journey().projectId);
    await expect.poll(() => files('terrabase-fra', created.id),
      { message: 'the removed follower erases its replica', timeout: 45_000, intervals: [250, 500, 1000] }).toEqual([]);
    const path = `/v1/sync/organizations/${journey().organizationId}/projects/${journey().projectId}/databases/${created.id}/placement`;
    await expect.poll(async () => (await catalogRecord<{ name: string }>('http://127.0.0.2:8081', path)).name,
      { message: 'the removed follower learns a rename without a placement version change', timeout: 45_000, intervals: [250, 500, 1000] }).toBe(renamed);
    await answer(await patch(cells, created.id, ['gru', 'fra']), 200, '200 follower added again');
    await expect.poll(async () => (await cells.get(`${replica}${database(created.id)}`)).status(),
      { message: 'the follower observes the placement that adds it again', timeout: 45_000, intervals: [250, 500, 1000] }).toBe(200);
    await replicated(cells, replica, created.id, 4);
    expect(await json(await cells.get(`${replica}${database(created.id)}`))).toMatchObject({ id: created.id, name: renamed });
    expect((await sql(cells, replica, created.id, 'SELECT value FROM placement_rows ORDER BY value')).results[0].rows).toEqual([[1], [2], [3], [4]]);
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('R18 deletion at the home retires a database while its follower is removed', async ({ cells }) => {
  const created = await create(cells, ['gru', 'fra']);
  let deleted = false;
  try {
    await seed(cells, created.id);
    await observed(cells, created.id);
    await replicated(cells, replica, created.id, 3);
    await answer(await patch(cells, created.id, ['gru']), 200, '200 follower removed');
    await expect.poll(() => files('terrabase-fra', created.id),
      { message: 'the follower erases its copy before deletion', timeout: 45_000, intervals: [250, 500, 1000] }).toEqual([]);
    expect(await json(await cells.get(`${home}${database(created.id)}`))).toMatchObject({ id: created.id, regions: ['gru'] });
    await answer(await cells.delete(`${home}${database(created.id)}`), 204, '204 deleted at the home');
    deleted = true;
    await expect.poll(async () => (await cells.get(`${replica}${database(created.id)}`)).status(),
      { message: 'the removed follower forgets the database after the home deletes it', timeout: 45_000, intervals: [250, 500, 1000] }).toBe(404);
    expect(files('terrabase-fra', created.id)).toEqual([]);
    const listed = await json<{ items: { id: string }[] }>(await cells.get(`${replica}${project()}/databases`));
    expect(listed.items.map(item => item.id)).not.toContain(created.id);
  } finally {
    if (!deleted) await removeDatabase(cells, created.id);
  }
});

test('R19 an unprimed non-writer in the home region cannot confirm deletion', async ({ cells }) => {
  const created = await create(cells, ['gru']);
  const directory = mkdtempSync(join(stateDir, 'non-writer-'));
  const source = readFileSync(join(stateDir, 'pids', 'terrabase.pid'), 'utf8').trim();
  const environment = Object.fromEntries(readFileSync(`/proc/${source}/environ`, 'utf8').split('\0').filter(Boolean).map(value => {
    const split = value.indexOf('=');
    return [value.slice(0, split), value.slice(split + 1)];
  }));
  writeFileSync(join(directory, 'peers'), 'journey-1 gru 127.0.0.1\njourney-unprimed gru 127.0.0.3\n', { mode: 0o600 });
  const token = randomBytes(32).toString('hex');
  saveSecret('non-writer-nats', token);
  writeFileSync(join(directory, 'nats-token'), token, { mode: 0o600 });
  const log = join(directory, 'node.log');
  const output = openSync(log, 'w', 0o600);
  const node = spawn('dotnet', [join(process.env.E2E_BUILD!, 'terrabase', 'terrabase.dll')], {
    env: {
      ...environment,
      TS_NODE: 'journey-unprimed',
      TS_PEERS_FILE: join(directory, 'peers'),
      TS_NATS_URL: 'nats://127.0.0.3:4222',
      TERRABASE_NATS_TOKEN_FILE: join(directory, 'nats-token'),
      TERRABASE_DATA_DIRECTORY: join(directory, 'data'),
      TERRABASE_PORT: '8280',
      TERRABASE_METRICS_PORT: '9291',
      TERRABASE_POSTGRES_PORT: '6632',
    },
    stdio: ['ignore', output, output],
  });
  closeSync(output);
  const exited = once(node, 'exit');
  const path = `/v1/sync/organizations/${journey().organizationId}/projects/${journey().projectId}/databases/${created.id}/placement`;
  try {
    await seed(cells, created.id);
    await expect.poll(() => {
      expect(node.exitCode, 'the real non-writer node stays running').toBeNull();
      return readFileSync(log, 'utf8').includes('Application started.');
    }, { message: 'the unprimed home-region node opens its listeners' }).toBe(true);
    expect(await catalogRecord('http://127.0.0.3:8081', '/v1/rollout/status')).toMatchObject({ node: 'journey-unprimed', writer: false });
    expect(await catalogRecord('http://127.0.0.1:8081', path)).toMatchObject({ databaseId: created.id, homeRegion: 'gru' });
    expect(await catalogRecord('http://127.0.0.3:8081', path, 503)).toMatchObject({ error: { code: 'not_writer' } });
    const missing = path.slice(0, -'/placement'.length - 1) + (created.id.endsWith('0') ? '1' : '0') + '/placement';
    expect(await catalogRecord('http://127.0.0.1:8081', missing, 404)).toMatchObject({ error: { code: 'not_found' } });
    expect(await catalogRecord('http://127.0.0.3:8081', missing, 503)).toMatchObject({ error: { code: 'not_writer' } });
    expect((await sql(cells, home, created.id, 'SELECT value FROM placement_rows ORDER BY value')).results[0].rows).toEqual([[1], [2], [3]]);
  } finally {
    node.kill('SIGTERM');
    await exited;
    await removeDatabase(cells, created.id);
  }
});


multiple('R20 receiving admission isolates organizations before any proxy work', async ({ cells, owner, request }) => {
  const first = await named(cells, replica, name('receiving-admission'), ['fra'], journey().projectId);
  const second = await named(owner.cells, replica, name('isolated-admission'), ['fra'], owner.projectId);
  const holding: ReturnType<typeof httpRequest>[] = [];
  const responses: number[] = [];
  try {
    await sql(cells, replica, first.id, 'CREATE TABLE placement_rows (value INTEGER PRIMARY KEY)');
    expect(postgresRows(first.id), 'a never-holding region validates and proxies a new Postgres session').toBe('');
    await sql(owner.cells, replica, second.id, 'CREATE TABLE placement_rows (value INTEGER PRIMARY KEY)', owner.projectId);
    await sql(owner.cells, replica, second.id, 'INSERT INTO placement_rows VALUES (7)', owner.projectId);
    const current = journey();
    const grant = await json<{ access_token: string }>(await request.post(`${auth}/terrascale/oauth/token`, {
      form: { grant_type: 'client_credentials', client_id: current.apiKey.slice(0, 26), client_secret: current.apiKey, project: current.projectId, scope: 'sql:read sql:write databases:read' },
    }));
    saveSecret('admission-isolation-token', grant.access_token);
    for (let index = 0; index < 4; index++) {
      const pending = httpRequest(`http://127.0.0.1:8080${database(first.id)}/sql/execute`, {
        method: 'POST',
        headers: { authorization: `Bearer ${grant.access_token}`, 'content-type': 'application/json', 'content-length': '1024' },
      });
      pending.on('response', response => { responses.push(response.statusCode ?? 0); response.resume(); });
      pending.on('error', error => { expect(['ECONNRESET', 'EPIPE']).toContain((error as NodeJS.ErrnoException).code); });
      holding.push(pending);
      pending.flushHeaders();
      pending.write('{');
    }
    await expect.poll(async () => {
      const response = await cells.post(`${home}${database(first.id)}/sql/execute`, { data: { statements: [{ sql: 'SELECT 1' }] } });
      if (response.status() === 200) return false;
      await answer(response, 429, 'tenant_busy');
      expect(Number(response.headers()['retry-after'])).toBeGreaterThan(0);
      return true;
    }, { message: `four incomplete requests occupy only their organization admission; holder responses ${JSON.stringify(responses)}` }).toBe(true);
    expect((await sql(owner.cells, home, second.id, 'SELECT value FROM placement_rows', owner.projectId)).results[0].rows).toEqual([[7]]);
    console.log('R20 admission: first organization has 4 receiving requests, next is 429 tenant_busy with Retry-After; second organization proxy is 200, rows [[7]]');
  } finally {
    const closed = holding.map(pending => pending.closed ? Promise.resolve() : new Promise<void>(resolve => pending.once('close', resolve)));
    for (const pending of holding) pending.destroy();
    await Promise.all(closed);
    console.log(`R20 incomplete holder responses: ${JSON.stringify(responses)}`);
    await removeDatabase(cells, first.id);
    await removeDatabase(owner.cells, second.id, owner.projectId);
  }
});


async function publishPlacementPlan(organization: string): Promise<void> {
  const credential = readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[0];
  const session = connect('http://127.0.0.1:8081');
  const stream = session.request({ ':method': 'PUT', ':path': `/admin/organizations/${organization}/plan`, 'x-terrabase-peer-credential': credential, 'content-type': 'application/json' });
  session.on('error', error => stream.destroy(error));
  try {
    const response = once(stream, 'response');
    stream.end(JSON.stringify({ plan: 'hobby' }));
    const [headers] = await response;
    let body = '';
    stream.setEncoding('utf8');
    for await (const chunk of stream) body += chunk;
    expect(Number(headers[':status']), body).toBe(200);
  } finally {
    session.close();
  }
}

test('R21 five-region writer moves retain every acknowledged write and refresh stale routes', async ({ cells }) => {
  const extra = ['sjc', 'sin', 'syd'];
  const restore = extendPeers(extra);
  const nodes: RegionProcess[] = [];
  const endpoints = new Map<string, string>([['gru', home], ['fra', replica]]);
  let created: Database | undefined;
  try {
    for (const [index, region] of extra.entries()) {
      const node = await startRegion(region, index);
      nodes.push(node);
      endpoints.set(region, node.endpoint);
    }
    await publishPlacementPlan(journey().organizationId);
    const selected = ['gru', 'fra', 'sin', 'sjc', 'syd'];
    created = await create(cells, selected);
    await sql(cells, home, created.id, 'CREATE TABLE placement_rows (value INTEGER PRIMARY KEY)');
    const acknowledged: number[] = [];
    for (let value = 1; value <= 3; value++) {
      await sql(cells, home, created.id, `INSERT INTO placement_rows VALUES (${value})`);
      acknowledged.push(value);
    }
    for (const region of selected.slice(1)) {
      await expect.poll(async () => {
        const response = await cells.post(`${endpoints.get(region)!}${database(created!.id)}/sql/execute`, { data: { statements: [{ sql: 'SELECT value FROM placement_rows ORDER BY value' }] } });
        const text = await response.text();
        let body: unknown;
        try { body = JSON.parse(text); } catch { throw new Error(`${region} readiness ${response.status()}: ${text}`); }
        return { status: response.status(), region: response.headers()['x-terrabase-region'], body };
      }, { message: `${region} holds all acknowledged rows`, timeout: 45_000, intervals: [250, 500, 1000] }).toMatchObject({ status: 200, region, body: { results: [{ rows: [[1], [2], [3]] }] } });
    }
    let writer = 'gru';
    let refreshed = 0;
    for (const [index, target] of selected.slice(1).entries()) {
      const remaining = selected.slice(index + 1);
      await expect.poll(async () => (await progress(cells, created!.id, endpoints.get(writer)!)).regions.find(row => row.region === target),
        { message: `${writer} observed a completed pull by ${target}`, timeout: 45_000, intervals: [250, 500, 1000] })
        .toMatchObject({ revision: expect.any(String), lastPulledAt: expect.any(String), lagMs: expect.any(Number) });
      await answer(await cells.patch(`${endpoints.get(writer)!}${database(created.id)}`, { data: { regions: remaining } }), 200, '200 writer moved');
      writer = target;
      const value = acknowledged.length + 1;
      const response = await cells.post(`${home}${database(created.id)}/sql/execute`, { data: { statements: [{ sql: `INSERT INTO placement_rows VALUES (${value})` }] } });
      await answer(response, 200, '200 one acknowledged write through the original region');
      expect(response.headers()['x-terrabase-region']).toBe(target);
      if ((response.headers()['server-timing']?.match(/home;dur=/g) ?? []).length === 2) refreshed++;
      acknowledged.push(value);
    }
    const found = (await sql(cells, endpoints.get(writer)!, created.id, 'SELECT value FROM placement_rows ORDER BY value')).results[0].rows.map(row => row[0]);
    expect(found).toEqual(acknowledged);
    expect(refreshed, 'a stale route was refused, refreshed and sent to a different region within the original request').toBeGreaterThan(0);
    console.log(`R21 five-region move: gru -> fra -> sin -> sjc -> syd; acknowledged ${acknowledged.length}; found ${found.length}; stale routes refreshed ${refreshed}; at most two one-hop attempts per request`);
  } finally {
    if (created) await removeDatabase(cells, created.id);
    for (const node of nodes.reverse()) await node.stop();
    restore();
  }
});

type RawUsage = { lifetime: { meters?: Record<string, { dimension: { meter: string; kind: string; region: string }; count: number }> } };

async function observationCounts(id: string): Promise<Record<string, Meter>> {
  const current = journey();
  const stores = ['http://127.0.0.1:8081', 'http://127.0.0.2:8081', 'http://10.228.0.2:8081'];
  const result: Record<string, Meter> = {};
  for (const region of ['gru', 'fra', 'sjc']) {
    const counts = await Promise.all(stores.map(async address => {
      const session = connect(address);
      const source = address.includes('127.0.0.1') ? 'fra' : 'gru';
      const query = new URLSearchParams({ organization: current.organizationId, project: current.projectId, database: id, region, source, range: '1h' });
      const stream = session.request({ ':path': '/v1/internal/usage/observations?' + query,
        'x-terrabase-peer-credential': readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[0] });
      session.on('error', error => stream.destroy(error));
      try {
        const response = once(stream, 'response');
        stream.end();
        const [headers] = await response;
        let body = '';
        for await (const chunk of stream) body += chunk;
        expect(Number(headers[':status']), address + ' observations: ' + body).toBe(200);
        const observed = JSON.parse(body) as RawUsage;
        return Object.values(observed.lifetime.meters ?? {}).reduce((sum, reading) => ({
          requests: sum.requests + (reading.dimension.meter === 'requests' ? reading.count : 0),
          operations: sum.operations + (reading.dimension.meter === 'ops' ? reading.count : 0),
          received: sum.received + (reading.dimension.meter === 'client_bytes' && reading.dimension.kind === 'received' ? reading.count : 0),
          sent: sum.sent + (reading.dimension.meter === 'client_bytes' && reading.dimension.kind === 'sent' ? reading.count : 0),
        }), { requests: 0, operations: 0, received: 0, sent: 0 });
      } finally {
        session.close();
      }
    }));
    result[region] = counts.reduce((sum, value) => ({ requests: sum.requests + value.requests, operations: sum.operations + value.operations,
      received: sum.received + value.received, sent: sum.sent + value.sent }), { requests: 0, operations: 0, received: 0, sent: 0 });
  }
  return result;
}

test('R3 replace: stale refusals are metered once at the source or final destination', async ({ cells }) => {
  const restore = extendPeers(['sjc']);
  let node: RegionProcess | undefined;
  try {
    node = await startRegion('sjc', 0);
    await publishPlacementPlan(journey().organizationId);
    for (const terminal of [true, false]) {
      let created: Database | undefined;
      let writer = replica;
      try {
        created = await named(cells, replica, name('stale-route'), ['fra', 'sjc'], journey().projectId);
        await sql(cells, replica, created.id, 'CREATE TABLE placement_rows (value INTEGER PRIMARY KEY)');
        await sql(cells, replica, created.id, 'INSERT INTO placement_rows VALUES (1), (2), (3)');
        await expect.poll(async () => (await progress(cells, created!.id, replica)).regions.find(row => row.region === 'sjc'),
          { message: 'the next writer has received every row', timeout: 45_000, intervals: [250, 500, 1000] })
          .toMatchObject({ revision: expect.any(String), lastPulledAt: expect.any(String) });
        await expect.poll(async () => {
          await sql(cells, home, created!.id, 'SELECT value FROM placement_rows');
          const response = await cells.patch(replica + database(created!.id), { data: { regions: ['sjc'] } });
          if (response.status() === 503) { await answer(response, 503, 'replica_not_ready'); return false; }
          await answer(response, 200, '200 the source cached fra before promotion');
          return true;
        }, { message: 'the final writer is ready while the source cache names fra', timeout: 45_000, intervals: [1000] }).toBe(true);
        writer = node.endpoint;
        const before = await observationCounts(created.id);
        const peers = join(stateDir, 'peers');
        const complete = readFileSync(peers, 'utf8');
        if (terminal) writeFileSync(peers, complete.replace(/^(journey-extra-sjc sjc) \S+$/gm, '$1 127.0.0.9'));
        try {
          const response = await cells.post(home + database(created.id) + '/sql/execute', {
            data: { statements: [{ sql: 'SELECT value FROM placement_rows ORDER BY value' }] },
          });
          if (terminal) {
            await answer(response, 503, 'unavailable');
            expect(response.headers()['retry-after']).toBe('1');
            expect((response.headers()['server-timing']?.match(/home;dur=/g) ?? []).length, 'one refused attempt and no reachable fresh destination').toBe(1);
          } else {
            await answer(response, 200, '200 after a refused stale attempt');
            expect((await response.json()).results[0].rows).toEqual([[1], [2], [3]]);
            expect((response.headers()['server-timing']?.match(/home;dur=/g) ?? []).length, 'a refusal and exactly one refreshed attempt').toBe(2);
          }
        } finally {
          writeFileSync(peers, complete);
        }
        const after = await observationCounts(created.id);
        const deltas = Object.fromEntries(['gru', 'fra', 'sjc'].map(region => [region, after[region].requests - before[region].requests]));
        expect(deltas, 'one final request owner across all stored observations').toEqual(terminal ? { gru: 1, fra: 0, sjc: 0 } : { gru: 0, fra: 0, sjc: 1 });
        const owner = terminal ? 'gru' : 'sjc';
        expect(after[owner].sent - before[owner].sent).toBeGreaterThan(0);
        expect(after[owner].received - before[owner].received).toBeGreaterThan(0);
        for (const region of ['gru', 'fra', 'sjc'].filter(region => region !== owner)) {
          expect(after[region].sent - before[region].sent, region + ' has no intermediate sent bytes').toBe(0);
          expect(after[region].received - before[region].received, region + ' has no intermediate received bytes').toBe(0);
        }
        console.log('R3 stale refusal: ' + JSON.stringify(deltas) + ', owner ' + owner + ' received and sent bytes >0; other bytes +0');
      } finally {
        if (created) await answer(await cells.delete(writer + database(created.id)), 204, '204 writer deletes its fixture');
      }
    }
  } finally {
    try {
      if (node) await node.stop();
    } finally {
      restore();
    }
  }
});
