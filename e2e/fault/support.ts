import { randomBytes } from 'node:crypto';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { appendFileSync, closeSync, openSync, readFileSync, writeFileSync } from 'node:fs';
import { request } from 'node:http';
import { connect } from 'node:http2';
import { join } from 'node:path';
import { expect, test, request as apiRequest, type APIRequestContext } from '@playwright/test';
import { database, json, sql, type Cells } from '../placement/support';
import { keyId } from '../support/api-key';
import { auth, localPort, r2StandIn, stateDir } from '../support/stack';
import { journey, ownerSession, saveSecret } from '../support/state';
import { signInToken } from '../support/auth-management';
import { request as addressedRequest } from '../support/client-address';

export const nodes = [
  { name: 'journey-1', region: 'gru', address: '127.0.0.1', port: 8080, postgres: 6432, metrics: 9091, service: 'terrabase' },
  { name: 'journey-2', region: 'fra', address: '127.0.0.2', port: 8180, postgres: 6532, metrics: 9191, service: 'terrabase-fra' },
  { name: 'journey-3', region: 'gru', address: '127.0.0.3', port: 8280, postgres: 6632, metrics: 9291, service: 'terrabase-gru-2' },
  { name: 'journey-4', region: 'fra', address: '127.0.0.4', port: 8380, postgres: 6732, metrics: 9391, service: 'terrabase-fra-2' },
] as const;
export type Node = typeof nodes[number];
export type Row = [number, string];
export const regions = ['gru', 'fra'] as const;
export const endpoint = (region: string) => `https://${region}.api.terrascale.tech`;
export const peersIn = (region: string) => nodes.filter(member => member.region === region);
export const node = (name: string) => nodes.find(candidate => candidate.name === name)!;

export type SystemBackup = {
  status: string; region: string; code?: string; message?: string; archivedThrough?: string;
  earliestRestorePoint?: string; latestRestorePoint?: string; lastSnapshotAt?: string; archivedBytes?: number; pendingSegments?: number;
};
export type NodeIdentity = { region: string; machine: string; regions: string[] };
export type Clock = { region: string; machine: string; wallMs: number; hlcWallMs: number; hlcLogical: number; hlcNode: number };

const nodeRequestTimeout = 15_000;

export async function nodeRequest(peer: { name: string; address: string }, path: string, method = 'GET', payload?: unknown, user?: string): Promise<{ status: number; body: string; headers: Record<string, string | string[] | undefined> }> {
  const credential = readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[0];
  const session = connect(`http://${peer.address}:${localPort('internal', 8081)}`);
  const stream = session.request({
    ':method': method, ':path': path, 'x-terrabase-peer-credential': credential,
    ...(payload === undefined ? {} : { 'content-type': 'application/json' }),
    ...(user ? { 'x-terrabase-user-id': user } : {}),
  });
  stream.setTimeout(nodeRequestTimeout, () => stream.destroy(new Error(`node request timed out for ${peer.name}: ${method} ${path.split('?')[0]}`)));
  session.on('error', error => stream.destroy(error));
  try {
    const response = once(stream, 'response');
    stream.end(payload === undefined ? undefined : JSON.stringify(payload));
    const [headers] = await response;
    let body = '';
    for await (const chunk of stream) body += chunk;
    return { status: Number(headers[':status']), body, headers };
  } finally {
    session.destroy();
  }
}

export async function identity(peer: { port: number }): Promise<NodeIdentity | undefined> {
  const response = await fetch(`http://127.0.0.1:${peer.port}/v1/regions`);
  if (response.status !== 200) return undefined;
  return response.json() as Promise<NodeIdentity>;
}

export async function clock(peer: Node): Promise<Clock> {
  const response = await nodeRequest(peer, '/v1/sync/clock');
  expect(response.status, response.body).toBe(200);
  return JSON.parse(response.body) as Clock;
}

export async function systemBackup(peer: Node): Promise<{ status: number; body: SystemBackup }> {
  const response = await nodeRequest(peer, '/v1/backup');
  return { status: response.status, body: response.body ? JSON.parse(response.body) as SystemBackup : { status: '', region: '' } };
}

export async function peersReady(): Promise<void> {
  for (const member of nodes) {
    await expect.poll(async () => {
      const ready = await fetch(`http://127.0.0.1:${member.port}/readyz`);
      if (ready.status !== 200) return { status: ready.status };
      const body = await ready.json() as { status: string; region: string };
      const listed = await identity(member);
      if (!listed) return { status: ready.status };
      return { status: body.status, region: body.region, machine: listed.machine, gru: listed.regions.includes('gru'), fra: listed.regions.includes('fra') };
    }, { message: `${member.name} is a ready active-active peer` }).toEqual({ status: 'ok', region: member.region, machine: member.name, gru: true, fra: true });
    const observed = await clock(member);
    expect(observed).toMatchObject({ region: member.region, machine: member.name });
  }
}

export async function projectToken(request: APIRequestContext): Promise<string> {
  const current = journey();
  const grant = await (await request.post(`${auth}/terrascale/oauth/token`, {
    form: { grant_type: 'client_credentials', client_id: keyId(current.apiKey), client_secret: current.apiKey, project: current.projectId, scope: 'sql:read sql:write databases:read databases:write' },
  })).json() as { access_token: string };
  saveSecret('fault-token', grant.access_token);
  return grant.access_token;
}

const signInReuse = 120_000;
let signedIn: { token: string; at: number } | undefined;

export async function signIn(): Promise<string> {
  if (signedIn && Date.now() - signedIn.at < signInReuse) return signedIn.token;
  const at = Date.now();
  const context = await addressedRequest.newContext({ storageState: ownerSession() });
  try {
    signedIn = { token: await signInToken(context, 'fault-sign-in'), at };
    return signedIn.token;
  } finally {
    await context.dispose();
  }
}

export async function cleanUp(failure: unknown, cleanup: () => Promise<void>): Promise<void> {
  try {
    await cleanup();
  } catch (error) {
    if (failure === undefined) throw error;
    await test.info().attach('cleanup error after the case failed', {
      body: error instanceof Error ? error.stack ?? error.message : String(error),
      contentType: 'text/plain',
    });
  }
}

export class Ledger {
  readonly acknowledged: Row[] = [];
  readonly submitted: Row[] = [];
  readonly failed: Row[] = [];
  readonly refused: Row[] = [];
  readonly path: string;

  constructor(readonly id: string) {
    this.path = join(stateDir, 'logs', `ledger-${id}.jsonl`);
  }

  record(event: string, rows: Row[], region: string): void {
    appendFileSync(this.path, JSON.stringify({ event, region, rows }) + '\n', { mode: 0o600 });
    if (event === 'submitted') this.submitted.push(...rows);
    if (event === 'acknowledged') this.acknowledged.push(...rows);
    if (event === 'failed') this.failed.push(...rows);
    if (event === 'refused') this.refused.push(...rows);
  }

  async direct(peer: Node, token: string, rows: Row[]): Promise<boolean> {
    this.record('submitted', rows, peer.region);
    try {
      const response = await fetch(`http://127.0.0.1:${peer.port}${database(this.id)}/sql/execute`, {
        method: 'POST', headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' },
        body: JSON.stringify({ statements: [{ sql: this.statement(rows) }] }), signal: AbortSignal.timeout(15_000),
      });
      const result = await response.json() as { results?: unknown[]; error?: { code: string } };
      if (response.status === 200 && response.headers.get('x-terrabase-region') === peer.region && response.headers.get('x-terrabase-machine') === peer.name && result.results?.length === 1) {
        this.record('acknowledged', rows, peer.region);
        return true;
      }
      this.record(response.status === 200 ? 'failed' : 'refused', rows, peer.region);
      return false;
    } catch {
      this.record('failed', rows, peer.region);
      return false;
    }
  }

  async readNode(peer: Node, token: string): Promise<Row[]> {
    const response = await fetch(`http://127.0.0.1:${peer.port}${database(this.id)}/sql/execute`, {
      method: 'POST', headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' },
      body: JSON.stringify({ statements: [{ sql: 'SELECT id, value FROM fault_rows ORDER BY id' }] }), signal: AbortSignal.timeout(15_000),
    });
    const body = await response.json() as { results?: { rows: Row[] }[]; error?: { code: string } };
    if (response.status === 404) return [];
    if (response.status === 400 && body.error?.code === 'undefined_table') return [];
    expect(response.status, `${peer.name} answers the read: ${JSON.stringify(body)}`).toBe(200);
    expect(response.headers.get('x-terrabase-region')).toBe(peer.region);
    expect(response.headers.get('x-terrabase-machine')).toBe(peer.name);
    return body.results![0].rows;
  }

  async converge(token: string, peers: readonly Node[], retained: Row[] = []): Promise<void> {
    await expect.poll(async () => {
      const seen = await Promise.all(peers.map(peer => this.readNode(peer, token)));
      for (const rows of seen) {
        for (const row of retained) expect(rows, 'a previously observed acknowledged write is never lost').toContainEqual(row);
        for (const row of rows) expect(this.submitted, 'no invented or changed write').toContainEqual(row);
      }
      return seen.every(rows => this.acknowledged.every(row => rows.some(actual => JSON.stringify(actual) === JSON.stringify(row))))
        && seen.every(rows => JSON.stringify(rows) === JSON.stringify(seen[0]));
    }, { message: 'every peer serves the client write ledger', timeout: 60_000, intervals: [250, 500, 1000] }).toBe(true);
    console.log(`ledger ${this.id}: ${this.acknowledged.length} acknowledged rows retained on ${peers.map(peer => peer.name).join(', ')}`);
  }

  async drop(token: string, peer: Node): Promise<void> {
    await expect.poll(async () => (await fetch(`http://127.0.0.1:${peer.port}${database(this.id)}`, {
      method: 'DELETE', headers: { authorization: `Bearer ${token}` }, signal: AbortSignal.timeout(15_000),
    })).status, { message: `database ${this.id} is deleted`, timeout: 60_000 }).toBe(204);
  }

  statement(rows: Row[]): string {
    return `INSERT INTO fault_rows (id, value) VALUES ${rows.map(([id, value]) => `(${id}, '${value}')`).join(',')}`;
  }

  async write(cells: Cells, region: string, rows: Row[]): Promise<void> {
    this.record('submitted', rows, region);
    await sql(cells, endpoint(region), this.id, this.statement(rows));
    this.record('acknowledged', rows, region);
  }

  async read(cells: Cells, region: string, id = this.id): Promise<Row[] | undefined> {
    const response = await cells.post(`${endpoint(region)}${database(id)}/sql/execute`, {
      data: { statements: [{ sql: 'SELECT id, value FROM fault_rows ORDER BY id' }] },
    });
    if (response.status() === 404) return [];
    if (response.status() === 400) {
      const error = await response.json();
      expect(error.error.code).toBe('undefined_table');
      return [];
    }
    if (response.status() === 200 && response.headers()['x-terrabase-region'] !== region) return undefined;
    return (await json<{ results: { rows: Row[] }[] }>(response)).results[0].rows;
  }

  async compare(cells: Cells, retained: Row[] = []): Promise<void> {
    await expect.poll(async () => {
      const seen = await Promise.all(regions.map(region => this.read(cells, region)));
      const served = seen.filter((rows): rows is Row[] => rows !== undefined);
      for (const rows of served) {
        for (const row of retained) expect(rows, 'a previously observed acknowledged write is never lost').toContainEqual(row);
        for (const row of rows) expect(this.submitted, 'no invented or changed write').toContainEqual(row);
      }
      return served.length === seen.length
        && served.every(rows => this.acknowledged.every(row => rows.some(actual => JSON.stringify(actual) === JSON.stringify(row))))
        && JSON.stringify(served[0]) === JSON.stringify(served[1]);
    }, { message: 'both regions converge to the client write ledger', timeout: 45_000, intervals: [250, 500, 1000] }).toBe(true);
    console.log(`ledger ${this.id}: ${this.acknowledged.length} acknowledged rows retained in both regions`);
  }
}

export function crashWrite(peer: Node, ledger: Ledger, token: string) {
  const rows: Row[] = Array.from({ length: 256 }, (_, index) => [index + 100, `inflight-${index}`]);
  ledger.record('submitted', rows, peer.region);
  let sent!: Promise<unknown>;
  const outcome = new Promise<void>((resolve, reject) => {
    const interrupted = (error: NodeJS.ErrnoException) => {
      if (error.code !== 'ECONNRESET' && error.code !== 'EPIPE') return reject(error);
      ledger.record(`unacknowledged-${error.code}`, rows, peer.region);
      resolve();
    };
    const call = request(`http://127.0.0.1:${peer.port}${database(ledger.id)}/sql/execute`, {
      method: 'POST',
      headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' },
      signal: AbortSignal.timeout(15_000),
    }, response => {
      let body = '';
      response.setEncoding('utf8');
      response.on('data', chunk => { body += chunk; });
      response.on('error', interrupted);
      response.on('end', () => {
        if (response.statusCode !== 200) return reject(new Error(`in-flight write: ${response.statusCode} ${body}`));
        try {
          const result = JSON.parse(body) as { results: unknown[] };
          expect(response.headers['x-terrabase-region']).toBe(peer.region);
          expect(response.headers['x-terrabase-machine']).toBe(peer.name);
          expect(result.results).toHaveLength(1);
          ledger.record('acknowledged', rows, peer.region);
          resolve();
        } catch (error) {
          reject(error);
        }
      });
    });
    sent = once(call, 'finish');
    call.on('error', interrupted);
    call.end(JSON.stringify({ statements: [{ sql: ledger.statement(rows) }] }));
  });
  return { sent, outcome: outcome.then(() => ({ error: undefined }), (error: Error) => ({ error })) };
}

export function restartAfterKill(peer: Node, access?: { token: string; path: string }, observe?: () => Promise<void>, build?: string, overrides?: NodeJS.ProcessEnv): () => Promise<void> {
  const pidFile = join(stateDir, 'pids', `${peer.service}.pid`);
  const pid = Number(readFileSync(pidFile, 'utf8').trim());
  const environment = Object.fromEntries(readFileSync(`/proc/${pid}/environ`, 'utf8').split('\0').filter(Boolean).map(value => {
    const at = value.indexOf('=');
    return [value.slice(0, at), value.slice(at + 1)];
  }));
  expect(environment.TS_NODE).toBe(peer.name);
  Object.assign(environment, overrides);
  const command = readFileSync(`/proc/${pid}/cmdline`, 'utf8').split('\0').filter(Boolean);
  process.kill(pid, 'SIGKILL');
  console.log(`SIGKILL ${peer.name}`);
  return async () => {
    await expect.poll(() => {
      try {
        return readFileSync(`/proc/${pid}/stat`, 'utf8').split(' ')[2] === 'Z';
      } catch (error) {
        expect((error as NodeJS.ErrnoException).code).toBe('ENOENT');
        return true;
      }
    }, { message: 'the killed process releases its data directory before restart' }).toBe(true);
    const log = join(stateDir, 'logs', `${peer.service}.log`);
    const offset = readFileSync(log).length;
    const output = openSync(log, 'a', 0o600);
    let exited = (_code: number | null, _signal: NodeJS.Signals | null) => {};
    const died = new Promise<never>((_, reject) => {
      exited = (code, signal) => reject(new Error(`the restarted ${peer.name} exited (${code ?? signal}) before it opened its listeners`));
    });
    const started = build === undefined ? command : [command[0], build];
    const child = spawn(started[0], started.slice(1), { env: environment, stdio: ['ignore', output, output] });
    try {
      child.once('exit', exited);
      await once(child, 'spawn');
      writeFileSync(pidFile, `${child.pid}\n`);
      child.unref();
    } finally {
      closeSync(output);
    }
    try {
      await Promise.race([died, expect.poll(() => readFileSync(log).subarray(offset).toString().includes('Application started.'), {
        message: 'the restarted peer opens its listeners',
      }).toBe(true)]);
    } finally {
      child.off('exit', exited);
    }
    await observe?.();
    await expect.poll(async () => {
      const response = await fetch(`http://127.0.0.1:${peer.port}/readyz`);
      if (response.status !== 200) return { status: response.status };
      const body = await response.json() as { status: string; region: string };
      const listed = await identity(peer);
      if (!listed) return { status: response.status };
      return { status: body.status, region: body.region, machine: listed.machine };
    }, { message: 'the restarted peer is ready in its region' }).toEqual({ status: 'ok', region: peer.region, machine: peer.name });
    if (access) {
      const context = await apiRequest.newContext({ extraHTTPHeaders: { authorization: `Bearer ${access.token}` } });
      try {
        await tokenReady(peer, context, access.path);
      } finally {
        await context.dispose();
      }
    }
  };
}

export async function scrape(peer: { address: string; port: number; metrics?: number; metricsPort?: number }): Promise<(metricName: string, label?: string) => number> {
  const response = await fetch(`http://${peer.address}:${peer.metricsPort ?? peer.metrics ?? peer.port + 1011}/metrics`);
  expect(response.status).toBe(200);
  const lines = (await response.text()).split('\n');
  return (metricName, label = '') => {
    const line = lines.find(text => new RegExp(`^${metricName}(\\{[^}]*\\})? `).test(text) && text.includes(label));
    return line ? Number(line.slice(line.lastIndexOf(' ') + 1)) : 0;
  };
}

export async function metric(peer: { address: string; port: number; metrics?: number; metricsPort?: number }, metricName: string, label = ''): Promise<number> {
  return (await scrape(peer))(metricName, label);
}

export async function probe(peer: { port: number }, path: '/readyz' | '/backupz' | '/healthz'): Promise<number> {
  return (await fetch(`http://127.0.0.1:${peer.port}${path}`)).status;
}

export async function backupStore(state: 'down' | 'up'): Promise<void> {
  const response = await fetch(`${r2StandIn}/fault/backups?state=${state}`, { method: 'POST' });
  expect(response.status).toBe(204);
  console.log(`backup object store ${state}`);
}

export async function backupPuts(prefix = ''): Promise<string[]> {
  const response = await fetch(`${r2StandIn}/fault/puts`);
  expect(response.status).toBe(200);
  const keys = await response.json() as string[];
  return prefix ? keys.filter(key => key.startsWith(prefix)) : keys;
}

export async function tokenReady(peer: Node, cells: Pick<Cells, 'get'>, path: string): Promise<void> {
  await expect.poll(async () => {
    const response = await cells.get(`http://127.0.0.1:${peer.port}${path}`);
    const status = response.status();
    await response.dispose();
    return status;
  }, { message: `${peer.name} must verify the existing token before authenticated recovery checks` }).toBe(200);
}

export function faultId(): string {
  const alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
  return '01' + Array.from(randomBytes(24), value => alphabet[value % 32]).join('');
}
