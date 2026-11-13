import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { appendFileSync, closeSync, openSync, readFileSync, writeFileSync } from 'node:fs';
import { request } from 'node:http';
import { connect } from 'node:http2';
import { createConnection } from 'node:net';
import { join } from 'node:path';
import { expect, type APIRequestContext } from '@playwright/test';
import { database, json, sql, type Cells } from '../placement/support';
import { auth, stateDir } from '../support/stack';
import { journey, saveSecret } from '../support/state';

export const nodes = [
  { name: 'journey-1', region: 'gru', address: '127.0.0.1', port: 8080, metrics: 9091, service: 'terrabase' },
  { name: 'journey-2', region: 'fra', address: '127.0.0.2', port: 8180, metrics: 9191, service: 'terrabase-fra' },
  { name: 'journey-3', region: 'gru', address: '127.0.0.3', port: 8280, metrics: 9291, service: 'terrabase-gru-2' },
  { name: 'journey-4', region: 'fra', address: '127.0.0.4', port: 8380, metrics: 9391, service: 'terrabase-fra-2' },
] as const;
export type Node = typeof nodes[number];
type NodeStatus = { node: string; owner: string; epoch: number; writer: boolean; ready: boolean };
export type Row = [number, string];
export type HomeLease = { organizationId: string; projectId: string; databaseId: string; home: string; node: string; epoch: number; session: string; adopted?: string | null };
export const node = (name: string) => nodes.find(candidate => candidate.name === name)!;
export const regions = ['gru', 'fra'] as const;
export const endpoint = (region: string) => `https://${region}.api.terrascale.tech`;

export async function peer(node: Node, path: string, body?: unknown): Promise<{ status: number; body: string }> {
  const credential = readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[0];
  const session = connect(`http://${node.address}:8081`);
  const payload = body === undefined ? undefined : JSON.stringify(body);
  const stream = session.request({
    ':path': path, ':method': payload === undefined ? 'GET' : 'POST', 'x-terrabase-peer-credential': credential,
    ...(payload === undefined ? {} : { 'content-type': 'application/json' }),
  });
  stream.setTimeout(60_000, () => stream.destroy(new Error(`peer request ${path} timed out for ${node.name}`)));
  session.on('error', error => stream.destroy(error));
  try {
    const response = once(stream, 'response');
    stream.end(payload);
    const [headers] = await response;
    let text = '';
    for await (const chunk of stream) text += chunk;
    return { status: Number(headers[':status']), body: text };
  } finally {
    session.destroy();
  }
}

export async function nodeStatus(node: Node): Promise<NodeStatus> {
  const answer = await peer(node, '/v1/rollout/status');
  expect(answer.status).toBe(200);
  return JSON.parse(answer.body) as NodeStatus;
}

export async function metric(node: Node, name: string, labels: Record<string, string> = {}): Promise<number> {
  const text = await (await fetch(`http://127.0.0.1:${node.metrics}/metrics`)).text();
  return text.split('\n').filter(line => line.startsWith(name + '{') || line.startsWith(name + ' '))
    .filter(line => Object.entries(labels).every(([key, value]) => line.includes(`${key}="${value}"`)))
    .reduce((sum, line) => sum + Number(line.slice(line.lastIndexOf(' ') + 1)), 0);
}

export function logged(node: Node, text: string): number {
  return readFileSync(join(stateDir, 'logs', `${node.service}.log`), 'utf8').split('\n').filter(line => line.includes(text)).length;
}

export async function writerToken(request: APIRequestContext): Promise<string> {
  const current = journey();
  const grant = await (await request.post(`${auth}/terrascale/oauth/token`, {
    form: { grant_type: 'client_credentials', client_id: current.apiKey.slice(0, 26), client_secret: current.apiKey, project: current.projectId, scope: 'sql:read sql:write databases:read databases:write' },
  })).json() as { access_token: string };
  saveSecret('fault-token', grant.access_token);
  return grant.access_token;
}

export class Kv {
  private buffer = Buffer.alloc(0);
  private readonly pending = new Map<string, (value: Record<string, unknown>) => void>();
  private next = 1;
  private readonly socket = createConnection({ port: 4222, host: '127.0.0.1' });

  private constructor() {
    this.socket.on('data', (chunk: Buffer) => {
      this.buffer = Buffer.concat([this.buffer, chunk]);
      for (;;) {
        const end = this.buffer.indexOf('\r\n');
        if (end < 0) return;
        const parts = this.buffer.subarray(0, end).toString().split(' ');
        if (parts[0] === 'MSG' || parts[0] === 'HMSG') {
          const size = Number(parts.at(-1));
          if (this.buffer.length < end + 2 + size + 2) return;
          const headerSize = parts[0] === 'HMSG' ? Number(parts.at(-2)) : 0;
          const data = this.buffer.subarray(end + 2 + headerSize, end + 2 + size).toString();
          const handler = this.pending.get(parts[2]);
          this.pending.delete(parts[2]);
          handler?.(data.startsWith('{') ? JSON.parse(data) : { raw: data });
          this.buffer = this.buffer.subarray(end + 2 + size + 2);
        } else {
          if (parts[0] === 'PING') this.socket.write('PONG\r\n');
          this.buffer = this.buffer.subarray(end + 2);
        }
      }
    });
  }

  static async open(): Promise<Kv> {
    const kv = new Kv();
    await once(kv.socket, 'connect');
    kv.socket.write('CONNECT ' + JSON.stringify({ auth_token: readFileSync(join(stateDir, 'nats-token'), 'utf8').trim(), headers: true, verbose: false }) + '\r\n');
    return kv;
  }

  private request(subject: string, payload: unknown, headers?: Record<string, string>): Promise<Record<string, unknown>> {
    const sid = String(this.next++);
    const inbox = `_INBOX.fault.${sid}`;
    const data = Buffer.from(JSON.stringify(payload));
    const answer = new Promise<Record<string, unknown>>((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(`NATS request ${subject} got no answer`)), 5000);
      this.pending.set(sid, value => { clearTimeout(timer); resolve(value); });
    });
    const block = headers ? Buffer.from('NATS/1.0\r\n' + Object.entries(headers).map(([key, value]) => `${key}: ${value}\r\n`).join('') + '\r\n') : Buffer.alloc(0);
    const publish = headers ? `HPUB ${subject} ${inbox} ${block.length} ${block.length + data.length}` : `PUB ${subject} ${inbox} ${data.length}`;
    this.socket.write(Buffer.concat([Buffer.from(`SUB ${inbox} ${sid}\r\nUNSUB ${sid} 1\r\n${publish}\r\n`), block, data, Buffer.from('\r\n')]));
    return answer;
  }

  async get<T>(bucket: string, key: string): Promise<{ seq: number; value: T } | null> {
    const stored = await this.request(`$JS.API.STREAM.MSG.GET.KV_${bucket}`, { last_by_subj: `$KV.${bucket}.${key}` }) as { message?: { seq: number; data?: string; hdrs?: string } };
    if (!stored.message?.data || Buffer.from(stored.message.hdrs ?? '', 'base64').toString().includes('KV-Operation')) return null;
    return { seq: stored.message.seq, value: JSON.parse(Buffer.from(stored.message.data, 'base64').toString()) as T };
  }

  async put(bucket: string, key: string, value: unknown, after: number): Promise<void> {
    const answer = await this.request(`$KV.${bucket}.${key}`, value, { 'Nats-Expected-Last-Subject-Sequence': String(after) });
    expect(answer.error, JSON.stringify(answer)).toBeUndefined();
  }

  async remove(bucket: string, key: string, after: number): Promise<void> {
    const answer = await this.request(`$KV.${bucket}.${key}`, {}, { 'KV-Operation': 'DEL', 'Nats-Expected-Last-Subject-Sequence': String(after) });
    expect(answer.error, JSON.stringify(answer)).toBeUndefined();
  }

  close(): void {
    this.socket.destroy();
  }
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
  readonly failed: Row[] = [];
  readonly path: string;

  constructor(readonly id: string) {
    this.path = join(stateDir, 'logs', `ledger-${id}.jsonl`);
  }

  record(event: string, rows: Row[], region: string): void {
    appendFileSync(this.path, JSON.stringify({ event, region, rows }) + '\n', { mode: 0o600 });
    if (event === 'submitted') this.submitted.push(...rows);
    if (event === 'acknowledged') this.acknowledged.push(...rows);
    if (event === 'failed') this.failed.push(...rows);
  }

  async direct(node: Node, token: string, rows: Row[]): Promise<boolean> {
    this.record('submitted', rows, node.region);
    try {
      const response = await fetch(`http://127.0.0.1:${node.port}${database(this.id)}/sql/execute`, {
        method: 'POST', headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' },
        body: JSON.stringify({ statements: [{ sql: this.statement(rows) }] }), signal: AbortSignal.timeout(15_000),
      });
      const result = await response.json() as { results?: { error?: unknown }[] };
      if (response.status === 200 && response.headers.get('x-terrabase-region') === node.region && result.results?.length === 1 && !result.results[0].error) {
        this.record('acknowledged', rows, node.region);
        return true;
      }
    } catch {
      this.record('failed', rows, node.region);
      return false;
    }
    this.record('failed', rows, node.region);
    return false;
  }

  async served(cells: Cells, region: string): Promise<Row[]> {
    const response = await cells.post(`${endpoint(region)}${database(this.id)}/sql/execute`, {
      data: { statements: [{ sql: 'SELECT id, value FROM fault_rows ORDER BY id' }] },
    });
    expect(response.status(), `${region} serves the database: ${await response.text()}`).toBe(200);
    expect(response.headers()['x-terrabase-region']).toBe(region);
    const rows = (await response.json() as { results: { rows: Row[] }[] }).results[0].rows;
    for (const row of rows) expect(this.submitted, 'no invented or changed write').toContainEqual(row);
    return rows;
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

export function restartAfterKill(node: Node): () => Promise<void> {
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
    await expect.poll(async () => (await nodeStatus(node)).writer, {
      message: 'the restarted node reacquires its fenced writer lease',
    }).toBe(true);
  };
}
