import { randomBytes } from 'node:crypto';
import { execFileSync, spawn } from 'node:child_process';
import { once } from 'node:events';
import { readFileSync } from 'node:fs';
import { connect } from 'node:http2';
import { join } from 'node:path';
import { expect, type Page } from '@playwright/test';
import { auth, stateDir } from '../support/stack';
import { journey, saveSecret } from '../support/state';
import { database, home, json, type Cells } from './support';

export const forgedHeaders = {
  'X-TerraBase-Proxied-Request': '1', 'X-TerraBase-Forwarded': '1',
  'X-TerraBase-Peer-Credential': 'customer-forgery', 'X-TerraBase-Forwarded-For': '192.0.2.1',
  'X-TerraBase-Remaining-Ms': '1', 'X-TerraBase-Request-Accounting': 'source',
  'X-TerraBase-Placement-Miss': '1', 'X-TerraBase-Usage-Scope': 'node',
};

export async function issueKey(organizationId: string, projectId: string, databaseId?: string): Promise<string> {
  const session = connect('http://127.0.0.1:8081');
  const stream = session.request({ ':method': 'POST', ':path': '/admin/api-keys', 'content-type': 'application/json',
    'x-terrabase-peer-credential': readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[0] });
  session.on('error', error => stream.destroy(error));
  try {
    const response = once(stream, 'response');
    stream.end(JSON.stringify({ name: 'placement restriction', scopes: ['sql:read', 'sql:write'], organizationId, projectId, databaseId }));
    const [headers] = await response;
    let body = '';
    for await (const chunk of stream) body += chunk;
    expect(Number(headers[':status']), 'issued a real scoped tb_ key').toBe(201);
    const secret = JSON.parse(body).secret as string;
    expect(secret.startsWith('tb_')).toBe(true);
    saveSecret('placement-tb-key', secret);
    return secret;
  } finally {
    session.close();
  }
}

async function destinationTraffic(request: Cells): Promise<{ discovery: number; connections: number; requests: number }> {
  const text = async () => (await request.get('http://127.0.0.1:9191/metrics')).text();
  const previous = await text();
  let current = previous;
  await expect.poll(async () => { current = await text(); return current !== previous; },
    { message: 'a fresh destination metrics snapshot', intervals: [50] }).toBe(true);
  const rows = current.split('\n');
  const sum = (prefix: string, matches: (line: string) => boolean) => rows.filter(line => line.startsWith(prefix) && matches(line))
    .reduce((total, line) => total + Number(line.split(' ').at(-1)), 0);
  const internal = (line: string) => line.includes('server_port="8081"');
  const discovery = sum('http_server_request_duration_seconds_count', line => line.includes('http_route="/v1/internal/home"'));
  const connections = sum('kestrel_connection_duration_seconds_count', internal) + sum('kestrel_active_connections', internal);
  const requests = sum('http_server_request_duration_seconds_count', line => /http_route="\/v1\/(internal\/(home|postgres)|projects\/)/.test(line));
  expect(rows.some(line => line.startsWith('http_server_request_duration_seconds_count')), 'HTTP request observations are exported').toBe(true);
  expect(rows.some(line => line.startsWith('kestrel_active_connections')), 'transport connection observations are exported').toBe(true);
  return { discovery, connections, requests };
}

async function browserAnswer(page: Page, id: string, token: string): Promise<string> {
  return page.evaluate(({ url, token }) => new Promise<string>((resolve, reject) => {
    const socket = new WebSocket(url, 'terrascale.sql.v1');
    socket.onopen = () => socket.send(JSON.stringify({ id: 1, type: 'auth', token }));
    socket.onerror = () => reject(new Error('the browser upgrade failed'));
    socket.onmessage = message => { const answer = JSON.parse(String(message.data)); socket.close(); resolve(answer.error?.code ?? answer.type); };
  }), { url: home.replace('https:', 'wss:') + database(id) + '/sql/ws', token });
}

function postgres(id: string, token: string) {
  return spawn('psql', ['-X', '-tA', '-v', 'ON_ERROR_STOP=1', '-v', 'VERBOSITY=verbose',
    'postgresql://any@pg.terrascale.tech:5432/' + journey().projectId + '/' + id + '?sslmode=verify-full&sslrootcert=' + encodeURIComponent(join(stateDir, 'pki', 'ca.pem'))], {
    env: { PATH: process.env.PATH, SSL_CERT_FILE: process.env.SSL_CERT_FILE, PGPASSWORD: token,
      PGSSLCERT: join(stateDir, 'postgres-client.crt'), PGSSLKEY: join(stateDir, 'postgres-client.key') },
    stdio: ['pipe', 'pipe', 'pipe'],
  });
}

export async function sourceRefusals(cells: Cells, request: Cells, page: Page, id: string, keys: Record<string, string>, ipToken?: string): Promise<void> {
  for (const [restriction, token] of Object.entries(keys)) {
    for (const protocol of ['REST', 'browser WebSocket', 'PostgreSQL']) {
      const before = await destinationTraffic(request);
      if (protocol === 'REST') {
        const response = await request.post(home + database(id) + '/sql/execute', {
          headers: { authorization: 'Bearer ' + token }, data: { statements: [{ sql: 'SELECT 1' }] },
        });
        expect.soft(response.status(), restriction + ' ' + protocol + ' refuses with the local answer').toBe(404);
        expect.soft((await response.json()).error.code).toBe('unknown_database');
      } else if (protocol === 'browser WebSocket') {
        expect.soft(await browserAnswer(page, id, token), restriction).toBe('unauthorized');
      } else {
        const process = postgres(id, token);
        let errors = '';
        process.stderr.on('data', chunk => { errors += chunk; });
        process.stdout.resume();
        const ended = once(process, 'close');
        process.stdin.end('SELECT 1;\n');
        const [status] = await ended;
        expect.soft(status, restriction).not.toBe(0);
        expect.soft(errors, restriction + ' PostgreSQL refuses before discovery').toContain('28000');
      }
      const after = await destinationTraffic(request);
      console.log('R3 ' + restriction + ' ' + protocol + ': destination deltas ' + JSON.stringify({ discovery: after.discovery - before.discovery, connections: after.connections - before.connections, requests: after.requests - before.requests }));
      expect.soft(after, restriction + ' ' + protocol + ': no destination discovery, request or accepted transport connection').toEqual(before);
    }
  }
  if (ipToken) {
    const before = await destinationTraffic(request);
    const response = await request.post(home + database(id) + '/sql/execute', {
      headers: { ...forgedHeaders, authorization: 'Bearer ' + ipToken }, data: { statements: [{ sql: 'SELECT 1' }] },
    });
    expect(response.status()).toBe(403);
    expect((await response.json()).error.code).toBe('ip_not_allowed');
    await page.setExtraHTTPHeaders(forgedHeaders);
    try {
      expect(await browserAnswer(page, id, ipToken)).toBe('unauthorized');
    } finally {
      await page.setExtraHTTPHeaders({});
    }
    expect(await destinationTraffic(request), 'forged client address cannot bypass the source IP restriction').toEqual(before);
    console.log('R3 forged internal headers: REST and browser IP restriction refused at source; destination +0');
    return;
  }
  const current = journey();
  const grant = await json<{ access_token: string }>(await request.post(auth + '/terrascale/oauth/token', {
    form: { grant_type: 'client_credentials', client_id: current.apiKey.slice(0, 26), client_secret: current.apiKey, project: current.projectId, scope: 'databases:read' },
  }));
  saveSecret('placement-no-sql-scope', grant.access_token);
  const before = await destinationTraffic(request);
  const response = await request.get(home + database(id) + '/sql/ws', { headers: {
    authorization: 'Bearer ' + grant.access_token, connection: 'Upgrade', upgrade: 'websocket',
    'sec-websocket-version': '13', 'sec-websocket-key': randomBytes(16).toString('base64'),
  } });
  expect.soft(response.status()).toBe(403);
  expect.soft((await response.json()).error.code).toBe('forbidden_scope');
  expect.soft(await destinationTraffic(request), 'header WebSocket without sql:read never reaches the destination').toEqual(before);
  expect((await cells.post(home + database(id) + '/sql/execute', { data: { statements: [{ sql: 'SELECT 1' }] } })).status()).toBe(200);
}

export async function unanswered(cells: Cells, page: Page, id: string, token: string): Promise<void> {
  const firewall = { env: { ...process.env, XTABLES_LOCKFILE: join(stateDir, 'iptables.lock') } };
  const rule = ['OUTPUT', '-p', 'tcp', '-s', '127.0.0.2', '--sport', '8081', '-d', '127.0.0.1', '-j', 'DROP'];
  for (const protocol of ['WebSocket', 'PostgreSQL']) {
    for (const mutating of [false, true]) {
      const value = protocol === 'WebSocket' ? 8101 : 8102;
      const sql = mutating ? 'INSERT INTO relay_outcomes VALUES (' + value + ')' : 'SELECT value FROM relay_outcomes';
      const pg = protocol === 'PostgreSQL' ? postgres(id, journey().apiKey) : undefined;
      let pgOutput = '';
      const ended = pg ? new Promise<number | null>(resolve => pg.once('close', resolve)) : undefined;
      try {
        if (pg) {
          const ready = new Promise<void>((resolve, reject) => {
            pg.stdout.on('data', chunk => { pgOutput += chunk; if (pgOutput.includes('relay_ready')) resolve(); });
            pg.stderr.on('data', chunk => { pgOutput += chunk; });
            pg.on('error', reject);
            pg.on('exit', () => reject(new Error('the Postgres session could not open')));
          });
          pg.stdin.write("SELECT 'relay_ready';\n");
          await ready;
        } else {
          await page.evaluate(({ url, token }) => new Promise<void>((resolve, reject) => {
            const socket = new WebSocket(url, 'terrascale.sql.v1');
            (window as Window & { outcomeSocket?: WebSocket }).outcomeSocket = socket;
            socket.onopen = () => socket.send(JSON.stringify({ id: 1, type: 'auth', token }));
            socket.onmessage = message => { const answer = JSON.parse(String(message.data)); if (answer.error) reject(new Error(answer.error.code)); else resolve(); };
            socket.onerror = () => reject(new Error('the relayed socket could not open'));
          }), { url: home.replace('https:', 'wss:') + database(id) + '/sql/ws', token });
        }
        execFileSync('iptables', ['-A', ...rule], firewall);
        if (pg) {
          pg.stdin.end(sql + ';\n');
          await ended;
          expect.soft(pgOutput, protocol + ' ' + (mutating ? 'write' : 'read')).toContain(mutating ? '08007' : '08006');
          if (!mutating) expect.soft(pgOutput).not.toContain('08007');
          console.log('R3 unanswered PostgreSQL ' + (mutating ? 'write' : 'read') + ': ' + (pgOutput.match(/\b08\d{3}\b/)?.[0] ?? 'no SQLSTATE'));
        } else {
          const code = await page.evaluate(sql => new Promise<string>((resolve, reject) => {
            const socket = (window as Window & { outcomeSocket?: WebSocket }).outcomeSocket!;
            socket.onmessage = message => { const answer = JSON.parse(String(message.data)); resolve(answer.error?.code ?? answer.type); socket.close(); };
            socket.onclose = () => reject(new Error('the relayed socket closed without an outcome'));
            socket.send(JSON.stringify({ id: 2, type: 'execute', statements: [{ sql }] }));
          }), sql);
          expect.soft(code, protocol + ' ' + (mutating ? 'write' : 'read')).toBe(mutating ? 'write_outcome_unknown' : 'unavailable');
          console.log('R3 unanswered WebSocket ' + (mutating ? 'write' : 'read') + ': ' + code);
        }
      } finally {
        execFileSync('iptables', ['-D', ...rule], firewall);
        if (pg && pg.exitCode === null) { pg.kill('SIGTERM'); await ended; }
      }
      if (mutating) {
        const result = await json<{ results: { rows: number[][] }[] }>(await cells.post('https://fra.api.terrascale.tech' + database(id) + '/sql/execute', {
          data: { statements: [{ sql: 'SELECT COUNT(*) FROM relay_outcomes WHERE value = ' + value }] },
        }));
        expect(result.results[0].rows, protocol + ': one execution after the destination answers again, no replay').toEqual([[1]]);
        console.log('R3 ' + protocol + ' unanswered write: destination execution count 1');
      }
    }
  }
}
