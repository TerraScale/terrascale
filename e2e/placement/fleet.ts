import { execFileSync, spawn } from 'node:child_process';
import { once } from 'node:events';
import { closeSync, mkdtempSync, openSync, readFileSync, readlinkSync, writeFileSync } from 'node:fs';
import { basename, join } from 'node:path';
import { expect } from '@playwright/test';
import { holdSync } from '../support/ports';
import { localPort, stateDir } from '../support/stack';

const meshPort = 8443;

export type RegionProcess = { region: string; endpoint: string; metricsPort: number; stop(): Promise<void> };

export async function startRegion(region: string, index: number): Promise<RegionProcess> {
  const source = readFileSync(join(stateDir, 'pids', 'terrabase.pid'), 'utf8').trim();
  const environment = Object.fromEntries(readFileSync(`/proc/${source}/environ`, 'utf8').split('\0').filter(Boolean).map(value => {
    const split = value.indexOf('=');
    return [value.slice(0, split), value.slice(split + 1)];
  }));
  const directory = mkdtempSync(join(stateDir, `region-${region}-`));
  const held = holdSync({
    client: 8380 + index * 100,
    metrics: 9391 + index * 100,
    postgres: 6732 + index * 100,
  });
  const port = held.ports.client;
  const address = `10.228.${index}.2`;
  const endpoint = `http://${address}:${port}`;
  const parent = `region${index}`;
  const child = `peer${index}`;
  const gate = join(directory, 'network-ready');
  execFileSync('mkfifo', [gate]);
  const log = join(stateDir, 'logs', `${basename(directory)}.log`);
  const output = openSync(log, 'w', 0o600);
  const node = spawn('unshare', ['--net', 'bash', '-c',
    'read -r ready < "$1"; exec dotnet "$2"', 'region', gate, join(process.env.E2E_BUILD!, 'terrabase', 'terrabase.dll')], {
    env: {
      ...environment, TS_REGION: region, TS_NODE: `journey-extra-${region}`,
      TERRABASE_DATA_DIRECTORY: join(stateDir, 'namespace', `region-data-${region}`),
      TERRABASE_PORT: String(port), TERRABASE_METRICS_PORT: String(held.ports.metrics),
      TERRABASE_POSTGRES_PORT: String(held.ports.postgres), TERRABASE_QUIC_PORT: String(meshPort),
    },
    stdio: ['ignore', output, output],
  });
  closeSync(output);
  const exited = once(node, 'exit');
  const stop = async () => {
    held.stop();
    if (node.exitCode === null) node.kill('SIGTERM');
    await exited;
    try { execFileSync('ip', ['link', 'del', parent], { stdio: 'pipe' }); } catch { }
  };
  try {
    await expect.poll(() => readlinkSync(`/proc/${node.pid}/ns/net`) !== readlinkSync('/proc/self/ns/net'),
      { message: `${region} has a distinct network namespace` }).toBe(true);
    try { execFileSync('ip', ['link', 'del', parent], { stdio: 'pipe' }); } catch { }
    execFileSync('ip', ['link', 'add', parent, 'type', 'veth', 'peer', 'name', child]);
    execFileSync('ip', ['link', 'set', child, 'netns', String(node.pid)]);
    execFileSync('ip', ['address', 'add', `10.228.${index}.1/30`, 'dev', parent]);
    execFileSync('ip', ['link', 'set', parent, 'up']);
    execFileSync('ip', ['route', 'add', `${address}/32`, 'dev', parent, 'src', '127.0.0.1']);
    const inside = (command: string, args: string[]) => execFileSync('nsenter', ['--target', String(node.pid), '--net', command, ...args]);
    inside('ip', ['link', 'set', 'lo', 'up']);
    inside('ip', ['address', 'add', `${address}/30`, 'dev', child]);
    inside('ip', ['link', 'set', child, 'up']);
    inside('ip', ['route', 'del', 'local', '127.0.0.0/8', 'table', 'local']);
    inside('ip', ['route', 'del', 'local', '127.0.0.1/32', 'table', 'local']);
    inside('ip', ['route', 'add', 'default', 'via', `10.228.${index}.1`]);
    inside('sysctl', ['-qw', 'net.ipv4.conf.all.route_localnet=1', 'net.ipv4.ip_nonlocal_bind=1']);
    writeFileSync(gate, 'ready\n');
    await expect.poll(async () => {
      expect(node.exitCode, `${region} stays running`).toBeNull();
      if (!readFileSync(log, 'utf8').includes('Application started.')) return null;
      const response = await fetch(`${endpoint}/readyz`);
      return response.status === 200 ? await response.json() as { status: string; region: string } : null;
    }, { message: `${region} is ready to serve reads and writes as an active-active peer` })
      .toMatchObject({ status: 'ok', region });
    return { region, endpoint, metricsPort: held.ports.metrics, stop };
  } catch (error) {
    await stop();
    throw error;
  }
}

export function extendPeers(regions: string[]): () => void {
  const path = join(stateDir, 'peers');
  const original = readFileSync(path, 'utf8');
  const forwarding = execFileSync('sysctl', ['-n', 'net.ipv4.ip_forward'], { encoding: 'utf8' }).trim();
  const loopback = execFileSync('sysctl', ['-n', 'net.ipv4.conf.all.route_localnet'], { encoding: 'utf8' }).trim();
  execFileSync('sysctl', ['-qw', 'net.ipv4.ip_forward=1', 'net.ipv4.conf.all.route_localnet=1']);
  writeFileSync(path, original + regions.map((region, index) => `journey-extra-${region} ${region} 10.228.${index}.2 ${meshPort} ${localPort('internal', 8081)}\n`).join(''));
  return () => {
    writeFileSync(path, original);
    execFileSync('sysctl', ['-qw', `net.ipv4.ip_forward=${forwarding}`, `net.ipv4.conf.all.route_localnet=${loopback}`]);
  };
}
