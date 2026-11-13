import { randomBytes } from 'node:crypto';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { once } from 'node:events';
import { appendFileSync, closeSync, existsSync, openSync, readFileSync, writeFileSync } from 'node:fs';
import { request } from 'node:http';
import { connect } from 'node:http2';
import { createConnection } from 'node:net';
import { join } from 'node:path';
import { expect, request as apiRequest, type APIRequestContext } from '@playwright/test';
import { database, json, sql, type Cells } from '../placement/support';
import { keyId } from '../support/api-key';
import { auth, r2StandIn, stateDir } from '../support/stack';
import { journey, saveSecret } from '../support/state';

export const nodes = [
  { name: 'journey-1', region: 'gru', address: '127.0.0.1', port: 8080, metrics: 9091, service: 'terrabase' },
  { name: 'journey-2', region: 'fra', address: '127.0.0.2', port: 8180, metrics: 9191, service: 'terrabase-fra' },
  { name: 'journey-3', region: 'gru', address: '127.0.0.3', port: 8280, metrics: 9291, service: 'terrabase-gru-2' },
  { name: 'journey-4', region: 'fra', address: '127.0.0.4', port: 8380, metrics: 9391, service: 'terrabase-fra-2' },
] as const;
export type KeptCopy = { id: string; organizationId: string; projectId: string; databaseId: string; revision?: string | null; reason: string; keptAt: string; files: string[]; bytes: number };
export type Unserved = { databaseId: string; region: string; node: string; epoch: number; cause: string };
export type HomeLease = { organizationId: string; projectId: string; databaseId: string; home: string; node: string; epoch: number; session: string; adopted?: string | null };
export const node = (name: string) => nodes.find(candidate => candidate.name === name)!;

export type Node = typeof nodes[number];
type NodeStatus = { node: string; owner: string; epoch: number; writer: boolean; ready: boolean; session: string; unserved?: Unserved[] | null; kept?: KeptCopy[] | null };
export type Row = [number, string];
export const regions = ['gru', 'fra'] as const;
export const endpoint = (region: string) => `https://${region}.api.terrascale.tech`;

export type BackupHole = { incarnation: string; from: number; to: number; fromAt: string | null; toAt: string | null; open: boolean };
export type BackupContinuity = {
  incarnation: string; historyStart: number | null; oldestRetained: number | null; storedPosition: number | null; storedIncarnation: string | null;
  replacing: boolean; holes: BackupHole[];
};

async function backupFiles(node: Node): Promise<{ file: string; continuity: BackupContinuity | null }[]> {
  const response = await nodeRequest(node, '/v1/backup');
  expect(response.status, response.body).toBe(200);
  return (JSON.parse(response.body) as { files: { file: string; continuity: BackupContinuity | null }[] }).files;
}

export async function continuity(node: Node, id: string): Promise<BackupContinuity & { file: string }> {
  const all = await backupFiles(node);
  const files = all.filter(file => file.file.includes(`/${id}/nodes/`));
  expect(files, JSON.stringify(all.map(file => file.file))).toHaveLength(1);
  expect(files[0]!.continuity).not.toBeNull();
  return { ...files[0]!.continuity!, file: files[0]!.file };
}

export async function nodeHoles(node: Node): Promise<string[]> {
  return (await backupFiles(node)).flatMap(file => (file.continuity?.holes ?? []).map(hole => `${file.file} ${hole.incarnation} ${hole.from}`));
}

export type HoleRecord = { incarnation: string; from: number; to: number; fromAt: number | null; toAt: number | null; open: boolean };

const time = (at: number | null) => at === null ? 'unknown' : String(at).padStart(13, '0');

export const holeKey = (file: string, hole: HoleRecord) =>
  `${file}/holes/${hole.incarnation}-${String(hole.from).padStart(16, '0')}-${String(hole.to).padStart(16, '0')}-${time(hole.fromAt)}-${time(hole.toAt)}-${hole.open ? 'open' : 'closed'}`;

export async function plantHole(file: string, hole: HoleRecord): Promise<void> {
  const response = await fetch(`${r2StandIn}/fault/plant?key=${encodeURIComponent(holeKey(file, hole))}`, { method: 'POST' });
  expect(response.status).toBe(204);
}

export async function holeRecords(state: 'unlisted' | 'listed'): Promise<void> {
  const response = await fetch(`${r2StandIn}/fault/holes?state=${state}`, { method: 'POST' });
  expect(response.status).toBe(204);
  console.log(`backup hole records ${state}`);
}

export async function backupPuts(file: string): Promise<string[]> {
  const response = await fetch(`${r2StandIn}/fault/puts`);
  expect(response.status).toBe(200);
  return (await response.json() as string[]).filter(key => key.startsWith(`${file}/`));
}

export async function nodeRequest(node: { name: string; address: string }, path: string, method = 'GET', payload?: unknown, user?: string): Promise<{ status: number; body: string }> {
  const credential = readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[0];
  const session = connect(`http://${node.address}:8081`);
  const stream = session.request({
    ':method': method, ':path': path, 'x-terrabase-peer-credential': credential,
    ...(payload === undefined ? {} : { 'content-type': 'application/json' }),
    ...(user ? { 'x-terrabase-user-id': user } : {}),
  });
  stream.setTimeout(60_000, () => stream.destroy(new Error(`status request timed out for ${node.name}`)));
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

export async function keptCopies(node: Node, id: string): Promise<KeptCopy[]> {
  return ((await nodeStatus(node)).kept ?? []).filter(copy => copy.databaseId === id);
}

export function keptHash(node: Node, copy: KeptCopy): string {
  const hash = createHash('sha256');
  for (const file of copy.files) hash.update(readFileSync(join(stateDir, node.service, '.kept', copy.id, file)));
  return hash.digest('hex');
}

export function holdsFile(node: Node, lease: { organizationId: string; projectId: string; databaseId: string }): boolean {
  return existsSync(join(stateDir, node.service, lease.organizationId, lease.projectId, `${lease.databaseId}.db`));
}

export function promoting(node: Node, id: string): boolean {
  return existsSync(join(stateDir, node.service, '.fences', 'promoting', id));
}

export async function counted(node: Node, name: string, labels: Record<string, string> = {}): Promise<number> {
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
    form: { grant_type: 'client_credentials', client_id: keyId(current.apiKey), client_secret: current.apiKey, project: current.projectId, scope: 'sql:read sql:write databases:read databases:write' },
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

  async publish(subject: string, payload: unknown, headers: Record<string, string>): Promise<Record<string, unknown>> {
    return this.request(subject, payload, headers);
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
      this.record(response.status === 200 ? 'failed' : 'refused', rows, node.region);
      return false;
    } catch {
      this.record('failed', rows, node.region);
      return false;
    }
  }

  async refusal(node: Node, token: string, rows: Row[]): Promise<{ status: number; code?: string }> {
    this.record('submitted', rows, node.region);
    const response = await fetch(`http://127.0.0.1:${node.port}${database(this.id)}/sql/execute`, {
      method: 'POST', headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' },
      body: JSON.stringify({ statements: [{ sql: this.statement(rows) }] }), signal: AbortSignal.timeout(45_000),
    });
    const body = await response.json() as { error?: { code: string }; results?: { error?: { code: string } }[] };
    const answer = { status: response.status, code: body.error?.code ?? body.results?.[0]?.error?.code };
    this.record(response.status === 200 && !answer.code ? 'acknowledged' : 'refused', rows, node.region);
    return answer;
  }

  async readNode(node: Node, token: string): Promise<Row[]> {
    const response = await fetch(`http://127.0.0.1:${node.port}${database(this.id)}/sql/execute`, {
      method: 'POST', headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' },
      body: JSON.stringify({ statements: [{ sql: 'SELECT id, value FROM fault_rows ORDER BY id' }] }), signal: AbortSignal.timeout(15_000),
    });
    if (response.status === 404 || response.status === 421) return [];
    const body = await response.json() as { results?: { rows: Row[] }[]; error?: { code: string } };
    if (response.status === 400 && body.error?.code === 'undefined_table') return [];
    expect(response.status, `${node.name} answers the read: ${JSON.stringify(body)}`).toBe(200);
    if (response.headers.get('x-terrabase-region') !== node.region) return [];
    return body.results![0].rows;
  }

  async converge(token: string, writers: Node[], retained: Row[] = []): Promise<void> {
    await expect.poll(async () => {
      const seen = await Promise.all(writers.map(writer => this.readNode(writer, token)));
      for (const rows of seen) {
        for (const row of retained) expect(rows, 'a previously observed acknowledged write is never lost').toContainEqual(row);
        for (const row of rows) expect(this.submitted, 'no invented or changed write').toContainEqual(row);
      }
      return seen.every(rows => this.acknowledged.every(row => rows.some(actual => JSON.stringify(actual) === JSON.stringify(row))))
        && seen.every(rows => JSON.stringify(rows) === JSON.stringify(seen[0]));
    }, { message: 'every region writer serves the client write ledger', timeout: 60_000, intervals: [250, 500, 1000] }).toBe(true);
    console.log(`ledger ${this.id}: ${this.acknowledged.length} acknowledged rows retained on ${writers.map(writer => writer.name).join(' and ')}`);
  }

  async drop(token: string, writer: Node): Promise<void> {
    await expect.poll(async () => (await fetch(`http://127.0.0.1:${writer.port}${database(this.id)}`, {
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

  async read(cells: Cells, region: string, id = this.id): Promise<Row[]> {
    const response = await cells.post(`${endpoint(region)}${database(id)}/sql/execute`, {
      data: { statements: [{ sql: 'SELECT id, value FROM fault_rows ORDER BY id' }] },
    });
    if (response.status() === 404) return [];
    if (response.status() === 400) {
      const error = await response.json();
      expect(error.error.code).toBe('undefined_table');
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
    let exited = (_code: number | null, _signal: NodeJS.Signals | null) => {};
    const died = new Promise<never>((_, reject) => {
      exited = (code, signal) => reject(new Error(`the restarted ${node.name} exited (${code ?? signal}) before it opened its listeners`));
    });
    const child = spawn(command[0], command.slice(1), { env: environment, stdio: ['ignore', output, output] });
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
        message: 'the restarted writer opens its listeners',
      }).toBe(true)]);
    } finally {
      child.off('exit', exited);
    }
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

export async function scrape(node: Node): Promise<(metricName: string, label?: string) => number> {
  const response = await fetch(`http://${node.address}:${node.port + 1011}/metrics`);
  expect(response.status).toBe(200);
  const lines = (await response.text()).split('\n');
  return (metricName, label = '') => {
    const line = lines.find(text => new RegExp(`^${metricName}(\\{[^}]*\\})? `).test(text) && text.includes(label));
    return line ? Number(line.slice(line.lastIndexOf(' ') + 1)) : 0;
  };
}

export async function metric(node: Node, metricName: string, label = ''): Promise<number> {
  return (await scrape(node))(metricName, label);
}

export async function probe(node: Node, path: '/readyz' | '/backupz'): Promise<number> {
  return (await fetch(`http://127.0.0.1:${node.port}${path}`)).status;
}

export async function backupStore(state: 'down' | 'up'): Promise<void> {
  const response = await fetch(`${r2StandIn}/fault/backups?state=${state}`, { method: 'POST' });
  expect(response.status).toBe(204);
  console.log(`backup object store ${state}`);
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
