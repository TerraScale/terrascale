import { spawn, type ChildProcess } from 'node:child_process';
import { once } from 'node:events';
import { mkdirSync, readFileSync, readdirSync, statSync, writeFileSync, createWriteStream } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { createInterface } from 'node:readline';
import { expect } from '@playwright/test';
import { stateDir } from '../support/stack';
import { saveSecret } from '../support/state';
import { faultId, nodeRequest } from './support';

class ReceiptNode {
  readonly process: ChildProcess;
  private readonly answers = new Map<string, { resolve: () => void; reject: (error: Error) => void }>();

  constructor(environment: NodeJS.ProcessEnv, directory: string, role: string) {
    const build = process.env.E2E_BUILD ?? join(stateDir, 'build');
    this.process = spawn('dotnet', [join(build, 'receipt-import', 'terrabase.dll')], {
      env: { ...environment, E2E_RECEIPT_ROLE: role }, stdio: ['pipe', 'pipe', 'pipe'],
    });
    const log = createWriteStream(join(directory, role + '.log'));
    this.process.stdout!.pipe(log);
    this.process.stderr!.pipe(log, { end: false });
    createInterface({ input: this.process.stdout! }).on('line', line => {
      if (line.startsWith('RECEIPT_NODE ')) this.answers.get(line.slice(13))?.resolve();
    });
    this.process.once('exit', (code, signal) => {
      const error = new Error('receipt node exited: ' + role + ' ' + code + ' ' + signal);
      for (const answer of this.answers.values()) answer.reject(error);
      log.end();
    });
    this.process.once('error', error => {
      for (const answer of this.answers.values()) answer.reject(error);
    });
  }

  async command(command: string): Promise<void> {
    try {
      await new Promise<void>((resolve, reject) => {
        this.answers.set(command, { resolve, reject });
        this.process.stdin!.write(command + '\n');
      });
    } finally {
      this.answers.delete(command);
    }
  }
}

export async function receiptHandoff(scenario: string): Promise<void> {
  const directory = join(stateDir, 'receipt-handoff-' + faultId());
  mkdirSync(directory, { recursive: true });
  const secrets = readFileSync(join(stateDir, 'secrets'), 'utf8').trim().split('\n');
  const [peer, master, , , , , , , , cloudflare, , , access, secret] = secrets;
  const token = faultId();
  saveSecret('receipt-nats-' + scenario, token);
  const peers = join(directory, 'peers');
  const suffix = faultId().toLowerCase();
  const sourceName = 'receipt-s-' + suffix;
  const targetName = 'receipt-t-' + suffix;
  writeFileSync(peers, sourceName + ' fra 127.0.0.5\n' + targetName + ' fra 127.0.0.6\n');
  const tools = process.env.E2E_TOOLS ?? join(homedir(), '.cache', 'terrascale-e2e');
  const natsLog = createWriteStream(join(directory, 'nats.log'));
  const configuration = join(directory, 'nats.conf');
  writeFileSync(configuration, 'listen: 127.0.0.1:4322\nhttp: 127.0.0.1:8322\nauthorization { token: ' + JSON.stringify(token) +
    ' }\njetstream { store_dir: ' + join(directory, 'nats') + ' }\n', { mode: 0o600 });
  const nats = spawn(join(tools, 'nats-server'), ['--config', configuration], { stdio: ['ignore', 'pipe', 'pipe'] });
  nats.stdout.pipe(natsLog);
  nats.stderr.pipe(natsLog, { end: false });
  const children: ReceiptNode[] = [];
  try {
    await new Promise<void>((resolve, reject) => {
      nats.stderr.on('data', (chunk: Buffer) => {
        if (chunk.toString().includes('Server is ready')) resolve();
      });
      nats.once('exit', code => reject(new Error('receipt NATS exited ' + code)));
      nats.once('error', reject);
    });
    const environment: NodeJS.ProcessEnv = { ...process.env };
    for (const key of Object.keys(environment)) {
      if (key.startsWith('TS_') || key.startsWith('TERRABASE_')) delete environment[key];
    }
    Object.assign(environment, {
      DOTNET_ENVIRONMENT: 'Production', ASPNETCORE_ENVIRONMENT: 'Production',
      TS_REGION: 'fra', TS_PEERS_FILE: peers, TS_NATS_URL: 'nats://127.0.0.1:4322', TERRABASE_NATS_TOKEN: token,
      TERRABASE_INTERNAL_PORT: '8081', TERRABASE_MASTER_KEY: master, TERRABASE_PEER_SECRET: peer,
      TERRABASE_AUTH_ISSUER: 'https://auth.terrascale.tech/terrascale', TERRABASE_AUTH_AUDIENCE: 'https://api.terrascale.tech',
      AWS_ENDPOINT_URL_S3: 'https://journey.backups.terrascale.tech/', AWS_REGION: 'auto', BUCKET_NAME: 'journey-backups',
      AWS_ACCESS_KEY_ID: access, AWS_SECRET_ACCESS_KEY: secret, TERRABASE_SHAPES_ACCESS_KEY_ID: access,
      TERRABASE_SHAPES_SECRET_ACCESS_KEY: secret, TERRABASE_SHAPES_CDN_API_TOKEN: cloudflare,
      TERRABASE_FILES_S3_ENDPOINT: 'https://journey.r2.cloudflarestorage.com/',
      TERRABASE_FILES_S3_ACCESS_KEY_ID: access, TERRABASE_FILES_S3_SECRET_ACCESS_KEY: secret,
    });
    const source = new ReceiptNode({ ...environment, TS_NODE: sourceName, TERRABASE_DATA_DIRECTORY: join(directory, 'source'),
      TERRABASE_PORT: '8480', TERRABASE_METRICS_PORT: '9491', TERRABASE_POSTGRES_PORT: '6832' }, directory, 'source');
    children.push(source);
    await source.command('ping');
    const sourceAddress = { name: sourceName, address: '127.0.0.5' };
    const targetAddress = { name: targetName, address: '127.0.0.6' };
    const status = async (address: typeof sourceAddress) => {
      const response = await nodeRequest(address, '/v1/rollout/status');
      expect(response.status, response.body).toBe(200);
      return JSON.parse(response.body) as { writer: boolean; owner: string; ready: boolean };
    };
    await expect.poll(() => status(sourceAddress)).toMatchObject({ writer: true, owner: sourceAddress.name, ready: true });
    const target = new ReceiptNode({ ...environment, TS_NODE: targetName, TERRABASE_DATA_DIRECTORY: join(directory, 'target'),
      TERRABASE_PORT: '8580', TERRABASE_METRICS_PORT: '9591', TERRABASE_POSTGRES_PORT: '6932' }, directory, 'target');
    children.push(target);
    await target.command('ping');
    await expect.poll(() => status(targetAddress)).toMatchObject({ writer: false, owner: sourceAddress.name, ready: true });
    const primed = await nodeRequest(targetAddress, '/v1/rollout/prime?source=' + sourceName, 'POST');
    expect(primed.status, primed.body).toBe(204);
    await target.command('stage identical');
    await source.command('stage ' + scenario);
    await target.command('capture');
    const previousKeys = scenario === 'identical' ? [
      ['00000000000000000000000003', '00000000000000000000000002'],
      ['00000000000000000000000004', '00000000000000000000000005'],
    ].map(([id, project]) => ({
      id, bytes: readFileSync(join(directory, 'target/00000000000000000000000001', project, id + '.db.key')),
    })) : [];
    const response = await nodeRequest(sourceAddress, '/v1/rollout/drain?target=' + targetName, 'POST');
    if (scenario === 'identical') {
      expect(response.status, response.body).toBe(204);
      await expect.poll(() => status(targetAddress)).toMatchObject({ writer: true, owner: targetAddress.name, ready: true });
      await target.command('receipts');
      const retained = join(directory, 'target/.handoff');
      const files = (path: string): string[] => readdirSync(path, { withFileTypes: true }).flatMap(entry =>
        entry.isDirectory() ? files(join(path, entry.name)) : [join(path, entry.name)]);
      const preserved = files(retained);
      for (const previous of previousKeys) {
        const key = preserved.find(path => path.endsWith('/' + previous.id + '.db.key'));
        expect(key, 'the superseded local database retains its key sidecar').toBeTruthy();
        expect(readFileSync(key!).equals(previous.bytes)).toBe(true);
        expect(statSync(key!.slice(0, -4)).size).toBeGreaterThan(0);
      }
      const metrics = await (await fetch('http://127.0.0.6:9591/metrics')).text();
      expect(metrics).toMatch(/terrabase_keys_local_wrapped[^\n]*key_id="k0"[^\n]*\} -1(?:\n|$)/);
    } else {
      expect(response.status, response.body).toBeGreaterThanOrEqual(400);
      await target.command('unchanged');
      await expect.poll(() => status(sourceAddress)).toMatchObject({ writer: true, owner: sourceAddress.name, ready: true });
      const user = faultId();
      const created = await nodeRequest(sourceAddress, '/v1/management/organizations', 'POST', { name: 'receipt-recovery-' + scenario }, user);
      expect(created.status, created.body).toBe(201);
      const id = (JSON.parse(created.body) as { id: string }).id;
      const read = await nodeRequest(sourceAddress, '/v1/management/organizations/' + id, 'GET', undefined, user);
      expect(read.status, read.body).toBe(200);
      expect((JSON.parse(read.body) as { id: string }).id).toBe(id);
      if (scenario !== 'rows' && scenario !== 'bytes') {
        const targetLog = readFileSync(join(directory, 'target.log'), 'utf8');
        expect(targetLog).toContain('Regional catalog import refused:');
        expect(targetLog).toContain('invalid or conflicting receipt records; catalog unchanged');
        const metricResponse = await fetch('http://127.0.0.6:9591/metrics');
        expect(metricResponse.status).toBe(200);
        const metrics = await metricResponse.text();
        const kind = scenario.startsWith('closure') || scenario.startsWith('stale') || scenario === 'absent-project'
          ? 'project_closure_receipts' : 'database_purge_receipts';
        const count = scenario === 'conflict-count' ? 2 : 1;
        const samples = metrics.split('\n').filter(line => line.startsWith('terrabase_regional_catalog_receipt_conflicts_total{') &&
          line.includes('kind="' + kind + '"'));
        expect(samples).toHaveLength(1);
        expect(Number(samples[0].split(' ').at(-1))).toBe(count);
        const conflictLine = targetLog.split('\n').find(line => line.includes('Regional catalog import refused:'))!;
        const logged = JSON.parse(conflictLine.slice(conflictLine.indexOf('{'))) as { Kind: string; Count: number };
        expect(logged).toMatchObject({ Kind: kind, Count: count });
      }
    }
    console.log('RECEIPT_HANDOFF ' + scenario + ': ' +
      (scenario === 'identical' ? 'target activated' : 'receipt bytes unchanged; snapshot sentinels absent; source writer restored; management write/read accepted'));
  } finally {
    await Promise.all(children.map(async child => {
      if (child.process.exitCode === null && child.process.signalCode === null) {
        const exited = once(child.process, 'exit');
        child.process.kill('SIGKILL');
        await exited;
      }
    }));
    if (nats.exitCode === null && nats.signalCode === null) {
      const exited = once(nats, 'exit');
      nats.kill('SIGKILL');
      await exited;
    }
    natsLog.end();
  }
}
