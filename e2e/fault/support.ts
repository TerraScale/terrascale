import { randomBytes } from 'node:crypto';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { appendFileSync, closeSync, openSync, readFileSync, writeFileSync } from 'node:fs';
import { request } from 'node:http';
import { connect } from 'node:http2';
import { join } from 'node:path';
import { expect, request as apiRequest } from '@playwright/test';
import { database, json, sql, type Cells } from '../placement/support';
import { stateDir } from '../support/stack';

export const nodes = [
  { name: 'journey-1', region: 'gru', address: '127.0.0.1', port: 8080, service: 'terrabase' },
  { name: 'journey-2', region: 'fra', address: '127.0.0.2', port: 8180, service: 'terrabase-fra' },
  { name: 'journey-3', region: 'gru', address: '127.0.0.3', port: 8280, service: 'terrabase-gru-2' },
  { name: 'journey-4', region: 'fra', address: '127.0.0.4', port: 8380, service: 'terrabase-fra-2' },
] as const;
type Node = typeof nodes[number];
type NodeStatus = { node: string; owner: string; epoch: number; writer: boolean; ready: boolean; session: string };
type Row = [number, string];
export const regions = ['gru', 'fra'] as const;
export const endpoint = (region: string) => `https://${region}.api.terrascale.tech`;

export async function nodeRequest(node: { name: string; address: string }, path: string, method = 'GET', payload?: unknown, user?: string): Promise<{ status: number; body: string }> {
  const credential = readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[0];
  const session = connect(`http://${node.address}:8081`);
  const stream = session.request({
    ':method': method, ':path': path, 'x-terrabase-peer-credential': credential,
    ...(payload === undefined ? {} : { 'content-type': 'application/json' }),
    ...(user ? { 'x-terrabase-user-id': user } : {}),
  });
  stream.setTimeout(15_000, () => stream.destroy(new Error(`status request timed out for ${node.name}`)));
  session.on('error', error => stream.destroy(error));
  try {
    const response = once(stream, 'response');
    stream.end(payload === undefined ? undefined : JSON.stringify(payload));
    const [headers] = await response;
    let body = '';
    for await (const chunk of stream) body += chunk;
    return { status: Number(headers[':status']), body };
  } finally {
    session.destroy();
  }
}

export async function nodeStatus(node: Node, region?: string): Promise<NodeStatus> {
  const response = await nodeRequest(node, `/v1/rollout/status${region ? `?region=${region}` : ''}`);
  expect(response.status).toBe(200);
  return JSON.parse(response.body) as NodeStatus;
}

export async function topology(): Promise<Node[]> {
  const writers: Node[] = [];
  for (const node of nodes) {
    const status = await nodeStatus(node);
    expect(status).toMatchObject({ node: node.name, ready: true });
    expect(nodes.filter(peer => peer.region === node.region).map(peer => peer.name)).toContain(status.owner);
    if (status.writer) {
      expect(status.owner).toBe(node.name);
      writers.push(node);
    }
  }
  for (const region of regions) expect(writers.filter(node => node.region === region)).toHaveLength(1);
  return writers;
}

export class Ledger {
  readonly acknowledged: Row[] = [];
  readonly submitted: Row[] = [];
  readonly path: string;

  constructor(readonly id: string) {
    this.path = join(stateDir, 'logs', `ledger-${id}.jsonl`);
  }

  record(event: string, rows: Row[], region: string): void {
    appendFileSync(this.path, JSON.stringify({ event, region, rows }) + '\n', { mode: 0o600 });
    if (event === 'submitted') this.submitted.push(...rows);
    if (event === 'acknowledged') this.acknowledged.push(...rows);
  }

  statement(rows: Row[]): string {
    return `INSERT INTO fault_rows (id, value) VALUES ${rows.map(([id, value]) => `(${id}, '${value}')`).join(',')}`;
  }

  async write(cells: Cells, region: string, rows: Row[]): Promise<void> {
    this.record('submitted', rows, region);
    await sql(cells, endpoint(region), this.id, this.statement(rows));
    this.record('acknowledged', rows, region);
  }

  async read(cells: Cells, region: string, id = this.id): Promise<Row[]> {
    const response = await cells.post(`${endpoint(region)}${database(id)}/sql/execute`, {
      data: { statements: [{ sql: 'SELECT id, value FROM fault_rows ORDER BY id' }] },
    });
    if (response.status() === 404) return [];
    if (response.status() === 400) {
      const error = await response.json();
      expect(error.error.code).toBe('sql_error');
      expect(error.error.message).toContain('no such table: fault_rows');
      return [];
    }
    if (response.status() === 200 && response.headers()['x-terrabase-region'] !== region) return [];
    return (await json<{ results: { rows: Row[] }[] }>(response)).results[0].rows;
  }

  async compare(cells: Cells, retained: Row[] = []): Promise<void> {
    await expect.poll(async () => {
      const seen = await Promise.all(regions.map(region => this.read(cells, region)));
      for (const rows of seen) {
        for (const row of retained) expect(rows, 'a previously observed acknowledged write is never lost').toContainEqual(row);
        for (const row of rows) expect(this.submitted, 'no invented or changed write').toContainEqual(row);
      }
      return seen.every(rows => this.acknowledged.every(row => rows.some(actual => JSON.stringify(actual) === JSON.stringify(row))))
        && JSON.stringify(seen[0]) === JSON.stringify(seen[1]);
    }, { message: 'both regions converge to the client write ledger', timeout: 45_000, intervals: [250, 500, 1000] }).toBe(true);
    console.log(`ledger ${this.id}: ${this.acknowledged.length} acknowledged rows retained in both regions`);
  }
}

export function crashWrite(node: Node, ledger: Ledger, token: string) {
  const rows: Row[] = Array.from({ length: 256 }, (_, index) => [index + 100, `inflight-${index}`]);
  ledger.record('submitted', rows, node.region);
  let sent!: Promise<unknown>;
  const outcome = new Promise<void>((resolve, reject) => {
    const interrupted = (error: NodeJS.ErrnoException) => {
      if (error.code !== 'ECONNRESET' && error.code !== 'EPIPE') return reject(error);
      ledger.record(`unacknowledged-${error.code}`, rows, node.region);
      resolve();
    };
    const call = request(`http://127.0.0.1:${node.port}${database(ledger.id)}/sql/execute`, {
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
          const result = JSON.parse(body) as { results: { error?: unknown }[] };
          expect(response.headers['x-terrabase-region']).toBe(node.region);
          expect(result.results).toHaveLength(1);
          expect(result.results[0].error).toBeUndefined();
          ledger.record('acknowledged', rows, node.region);
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

export function restartAfterKill(node: Node, writer = true, access?: { token: string; path: string }, observe?: () => Promise<void>): () => Promise<void> {
  const pidFile = join(stateDir, 'pids', `${node.service}.pid`);
  const pid = Number(readFileSync(pidFile, 'utf8').trim());
  const environment = Object.fromEntries(readFileSync(`/proc/${pid}/environ`, 'utf8').split('\0').filter(Boolean).map(value => {
    const at = value.indexOf('=');
    return [value.slice(0, at), value.slice(at + 1)];
  }));
  expect(environment.TS_NODE).toBe(node.name);
  const command = readFileSync(`/proc/${pid}/cmdline`, 'utf8').split('\0').filter(Boolean);
  process.kill(pid, 'SIGKILL');
  console.log(`SIGKILL ${node.name}: client write flushed, response outstanding`);
  return async () => {
    await expect.poll(() => {
      try {
        return readFileSync(`/proc/${pid}/stat`, 'utf8').split(' ')[2] === 'Z';
      } catch (error) {
        expect((error as NodeJS.ErrnoException).code).toBe('ENOENT');
        return true;
      }
    }, { message: 'the killed process releases its data directory before restart' }).toBe(true);
    const log = join(stateDir, 'logs', `${node.service}.log`);
    const offset = readFileSync(log).length;
    const output = openSync(log, 'a', 0o600);
    try {
      const child = spawn(command[0], command.slice(1), { env: environment, stdio: ['ignore', output, output] });
      child.on('error', error => { throw error; });
      writeFileSync(pidFile, `${child.pid}\n`);
      child.unref();
    } finally {
      closeSync(output);
    }
    await expect.poll(() => readFileSync(log).subarray(offset).toString().includes('Application started.'), {
      message: 'the restarted writer opens its listeners',
    }).toBe(true);
    await observe?.();
    await expect.poll(() => nodeStatus(node), {
      message: 'the restarted node restores its ready regional role',
    }).toMatchObject({ writer, ready: true });
    if (access) {
      const context = await apiRequest.newContext({ extraHTTPHeaders: { authorization: `Bearer ${access.token}` } });
      try {
        await tokenReady(node, context, access.path);
      } finally {
        await context.dispose();
      }
    }
  };
}

export async function tokenReady(node: Node, cells: Pick<Cells, 'get'>, path: string): Promise<void> {
  await expect.poll(async () => {
    const response = await cells.get(`http://127.0.0.1:${node.port}${path}`);
    const status = response.status();
    await response.dispose();
    return status;
  }, { message: `${node.name} must verify the existing token before authenticated recovery checks` }).toBe(200);
}

export function faultId(): string {
  const alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
  return '01' + Array.from(randomBytes(24), value => alphabet[value % 32]).join('');
}
