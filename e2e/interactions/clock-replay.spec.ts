import { randomBytes } from 'node:crypto';
import { spawn } from 'node:child_process';
import { createSocket } from 'node:dgram';
import { once } from 'node:events';
import { closeSync, mkdtempSync, openSync, readFileSync, renameSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { test, expect } from '@playwright/test';
import { saveSecret } from '../support/state';
import { e2eRoot, production } from '../support/stack';

test.skip(production, 'clock replay runs only against a throwaway node');

function chronyFloat(value: number): number {
  if (value === 0) return 0;
  const exponent = Math.floor(Math.log2(Math.abs(value))) - 23;
  return (((exponent + 25) << 25) | (Math.round(value / 2 ** exponent) & 0x1ffffff)) >>> 0;
}

test('clock admission stays closed across stale tracking samples after a wall-clock step', async ({ request }, info) => {
  const root = mkdtempSync(join(tmpdir(), 'terrabase-clock-replay-'));
  const input = join(root, 'reading');
  const library = join(root, 'libclock-replay.so');
  const log = openSync(join(root, 'node.log'), 'w');
  const daemon = createSocket('udp4');
  let reference = Math.floor(Date.now() / 1000) - 1;
  let correction = 0;
  let samples = 0;
  let node: ReturnType<typeof spawn> | undefined;
  const observations: string[] = [];
  const replies: string[] = [];
  const peerSecret = randomBytes(32).toString('hex');
  const masterKey = randomBytes(32).toString('base64');
  saveSecret('clock-replay-peer', peerSecret);
  saveSecret('clock-replay-master', masterKey);
  const set = (offset: number, status = 0, error = 1000) => {
    writeFileSync(`${input}.next`, `${offset} ${status} ${error}\n`);
    renameSync(`${input}.next`, input);
  };
  const gauge = async () => {
    expect(node?.exitCode, readFileSync(join(root, 'node.log'), 'utf8')).toBeNull();
    const response = await request.get('http://127.0.0.1:19091/metrics');
    expect(response.ok()).toBe(true);
    const value = /^terrabase_clock_local_writes_allowed(?:\{[^}]*\})? (\S+)/m.exec(await response.text());
    return value ? Number(value[1]) : undefined;
  };
  const allowed = async (value: number, label: string) => {
    await expect.poll(gauge, { message: label }).toBe(value);
    observations.push(`${label}: local_writes_allowed=${value}, tracking_samples=${samples}`);
  };
  try {
    set(600_000);
    const compiler = spawn('cc', ['-shared', '-fPIC', '-Wall', '-Wextra', '-Werror', join(e2eRoot, 'clock/replay.c'), '-o', library, '-ldl']);
    let compileErrors = '';
    compiler.stderr.on('data', (chunk: Buffer) => { compileErrors += chunk.toString(); });
    expect((await once(compiler, 'exit'))[0], compileErrors).toBe(0);
    symlinkSync(library, join(root, 'libc.so'));
    daemon.on('message', (query, peer) => {
      const answer = Buffer.alloc(104);
      answer[0] = 6;
      answer[1] = 2;
      answer.writeUInt16BE(33, 4);
      answer.writeUInt16BE(5, 6);
      query.copy(answer, 16, 8, 12);
      answer.writeUInt16BE(2, 52);
      answer.writeBigUInt64BE(BigInt(reference), 56);
      answer.writeUInt32BE(chronyFloat(correction / 1000), 68);
      answer.writeUInt32BE(chronyFloat(0.001), 92);
      answer.writeUInt32BE(chronyFloat(0.001), 96);
      replies.push(answer.toString('hex'));
      daemon.send(answer, peer.port, peer.address);
      samples++;
    });
    daemon.bind(13233, '127.0.0.1');
    await once(daemon, 'listening');
    const build = process.env.E2E_BUILD ? join(process.env.E2E_BUILD, 'terrabase') : resolve(e2eRoot, '../src/TerraBase/bin/Release/net11.0');
    const environment = { ...process.env };
    for (const name of ['TS_PEERS_FILE', 'TS_REGION', 'TS_NODE']) delete environment[name];
    node = spawn('nice', ['-n', '19', 'dotnet', join(build, 'terrabase.dll')], {
      env: {
        ...environment, DOTNET_ENVIRONMENT: 'Development', DOTNET_PROCESSOR_COUNT: '4',
        TERRABASE_DATA_DIRECTORY: join(root, 'data'), TERRABASE_PORT: '18080',
        TERRABASE_INTERNAL_PORT: '18081', TERRABASE_METRICS_PORT: '19091', TERRABASE_POSTGRES_PORT: '16432',
        TERRABASE_AUTH_ISSUER: 'https://auth.terrascale.tech/terrascale', TERRABASE_AUTH_AUDIENCE: 'https://api.terrascale.tech',
        TERRABASE_PEER_SECRET: peerSecret, TERRABASE_MASTER_KEY: masterKey,
        LD_PRELOAD: library, LD_LIBRARY_PATH: `${root}:${process.env.LD_LIBRARY_PATH ?? ''}`, E2E_CLOCK_REPLAY: input,
      }, stdio: ['ignore', log, log],
    });
    await expect.poll(async () => {
      expect(node?.exitCode, readFileSync(join(root, 'node.log'), 'utf8')).toBeNull();
      return request.get('http://127.0.0.1:18080/readyz').then((response) => response.status(), () => 0);
    }).toBe(200);
    await allowed(0, 'start during a step with the first unchanged daemon reference');
    expect(samples).toBe(1);
    const atStart = samples;
    await expect.poll(async () => {
      expect(await gauge()).toBe(0);
      return samples - atStart;
    }, { message: 'startup remains closed through two unchanged-reference replies' }).toBeGreaterThanOrEqual(2);
    observations.push('startup with unchanged reference after two more samples: local_writes_allowed=0');
    reference++;
    correction = -600_000;
    const beforeStartupMeasurement = samples;
    await expect.poll(() => samples).toBeGreaterThan(beforeStartupMeasurement);
    await allowed(0, 'new startup measurement reports the existing step offset');
    set(0);
    reference++;
    correction = 0;
    await allowed(1, 'startup clock corrected with a strictly newer reference');
    set(600_000);
    await allowed(0, 'step detected between samples');
    const afterStep = samples;
    await expect.poll(async () => {
      expect(await gauge()).toBe(0);
      return samples - afterStep;
    }, { message: 'two further tracking replies with the same reference cannot reopen admission' }).toBeGreaterThanOrEqual(2);
    observations.push('same reference after two more samples: local_writes_allowed=0');
    reference++;
    correction = -600_000;
    const beforeMeasurement = samples;
    await expect.poll(() => samples).toBeGreaterThan(beforeMeasurement);
    await allowed(0, 'new measurement still reports the step offset');
    set(0);
    reference++;
    correction = 0;
    await allowed(1, 'corrected clock and new daemon measurement');
    set(0, 64);
    await allowed(0, 'kernel unsynchronized despite healthy tracking');
    set(0, 0, 50_001);
    await allowed(0, 'kernel error bound exceeds 50 ms');
    set(0);
    await allowed(1, 'kernel synchronized within bound');
    console.log(observations.join('\n'));
  } finally {
    if (node && node.exitCode === null) {
      node.kill('SIGTERM');
      await once(node, 'exit');
    }
    daemon.close();
    closeSync(log);
    await info.attach('clock-replay', { body: observations.join('\n'), contentType: 'text/plain' });
    await info.attach('tracking-replies', { body: replies.join('\n'), contentType: 'text/plain' });
    rmSync(root, { recursive: true, force: true });
  }
});
