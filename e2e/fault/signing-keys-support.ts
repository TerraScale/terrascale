import { spawn, type ChildProcess } from 'node:child_process';
import { once } from 'node:events';
import { closeSync, mkdirSync, mkdtempSync, openSync, readFileSync, renameSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { createServer as httpServer } from 'node:http';
import { createServer as httpsServer, request as httpsRequest } from 'node:https';
import { connect } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, type APIRequestContext } from '@playwright/test';
import { e2eRoot, stateDir } from '../support/stack';

export async function signingNode(request: APIRequestContext) {
  const root = mkdtempSync(join(tmpdir(), 'signing-keys-'));
  const data = join(root, 'data');
  mkdirSync(data);
  const stored = join(data, 'auth-signing-keys.json');
  const clock = join(root, 'clock');
  const library = join(root, 'libclock-replay.so');
  const compile = spawn('cc', ['-shared', '-fPIC', '-Wall', '-Wextra', '-Werror', join(e2eRoot, 'clock/replay.c'), '-o', library, '-ldl']);
  expect((await once(compile, 'exit'))[0]).toBe(0);
  symlinkSync(library, join(root, 'libc.so'));
  const setClock = (offset: number) => {
    writeFileSync(clock + '.next', `${offset} 0 1000\n`);
    renameSync(clock + '.next', clock);
  };
  setClock(0);
  const original = Number(readFileSync(join(stateDir, 'pids/terrabase.pid'), 'utf8').trim());
  const environment = Object.fromEntries(readFileSync(`/proc/${original}/environ`, 'utf8').split('\0').filter(Boolean).map(value => {
    const at = value.indexOf('=');
    return [value.slice(0, at), value.slice(at + 1)];
  }));
  delete environment.TS_PEERS_FILE;
  delete environment.TS_REGION;
  delete environment.TS_NODE;
  delete environment.TS_NATS_URL;
  delete environment.TERRABASE_NATS_TOKEN_FILE;
  for (const name of Object.keys(environment)) {
    if (name.startsWith('AWS_') || name === 'BUCKET_NAME') delete environment[name];
  }
  const command = readFileSync(`/proc/${original}/cmdline`, 'utf8').split('\0').filter(Boolean);
  let child: ChildProcess | undefined;
  let unavailable = false;
  let keysUnavailable = false;
  let additions: Record<string, unknown>[] = [];
  let replace: Record<string, unknown>[] | undefined;
  let fetched = 0;
  let tokenTransform: ((value: string) => string) | undefined;
  let held: (() => void) | undefined;
  let holdNext = false;
  const tls = httpsServer({ key: readFileSync(join(stateDir, 'pki/edge.key')), cert: readFileSync(join(stateDir, 'pki/edge.pem')) }, (incoming, outgoing) => {
    if (unavailable && /(?:well-known|jwks|oauth\/token)/.test(incoming.url ?? '')
      || keysUnavailable && /(?:well-known|jwks)/.test(incoming.url ?? '')) {
      outgoing.writeHead(503).end();
      return;
    }
    const upstream = httpsRequest(`https://auth.terrascale.tech${incoming.url}`, {
      method: incoming.method, headers: { ...incoming.headers, host: 'auth.terrascale.tech', 'accept-encoding': 'identity' },
    }, response => {
      const chunks: Buffer[] = [];
      response.on('data', chunk => chunks.push(Buffer.from(chunk)));
      response.on('end', () => {
        let body = Buffer.concat(chunks);
        if (response.statusCode === 200 && response.headers['content-type']?.includes('json')) {
          const value = JSON.parse(body.toString());
          if (Array.isArray(value.keys)) {
            fetched++;
            value.keys = replace ?? [...value.keys, ...additions];
            body = Buffer.from(JSON.stringify(value));
          }
          if (typeof value.access_token === 'string' && tokenTransform) {
            value.access_token = tokenTransform(value.access_token);
            body = Buffer.from(JSON.stringify(value));
          }
        }
        const send = () => {
          const headers = { ...response.headers };
          delete headers['transfer-encoding'];
          delete headers['content-length'];
          delete headers['content-encoding'];
          outgoing.writeHead(response.statusCode ?? 502, { ...headers, 'content-length': body.length }).end(body);
        };
        if (holdNext && response.headers['content-type']?.includes('json') && Array.isArray(JSON.parse(body.toString()).keys)) {
          holdNext = false;
          held = send;
        } else send();
      });
    });
    upstream.on('error', () => outgoing.writeHead(502).end());
    incoming.pipe(upstream);
  });
  const proxy = httpServer();
  const tunnels = new Set<import('node:stream').Duplex>();
  proxy.on('connect', (incoming, socket, head) => {
    tunnels.add(socket);
    socket.on('close', () => tunnels.delete(socket));
    if (incoming.url === 'auth.terrascale.tech:443') {
      socket.write('HTTP/1.1 200 Connection Established\r\n\r\n');
      if (head.length) socket.unshift(head);
      tls.emit('connection', socket);
    } else {
      const [host, port] = (incoming.url ?? '').split(':');
      const upstream = connect(Number(port), host, () => {
        socket.write('HTTP/1.1 200 Connection Established\r\n\r\n');
        if (head.length) upstream.write(head);
        socket.pipe(upstream).pipe(socket);
      });
      upstream.on('error', () => socket.destroy());
    }
  });
  proxy.listen(18789, '127.0.0.1');
  await once(proxy, 'listening');
  const log = () => readFileSync(join(root, 'node.log'), 'utf8');
  const start = async () => {
    const output = openSync(join(root, 'node.log'), 'a', 0o600);
    child = spawn('nice', ['-n', '19', ...command], {
      env: { ...environment, DOTNET_PROCESSOR_COUNT: '2', DOTNET_ENVIRONMENT: 'Development', ASPNETCORE_ENVIRONMENT: 'Development',
        TERRABASE_DATA_DIRECTORY: data, TERRABASE_PORT: '18780', TERRABASE_INTERNAL_PORT: '18781',
        TERRABASE_METRICS_PORT: '19780', TERRABASE_POSTGRES_PORT: '16780',
        HTTPS_PROXY: 'http://127.0.0.1:18789', https_proxy: 'http://127.0.0.1:18789', NO_PROXY: '', no_proxy: '',
        LD_PRELOAD: library, LD_LIBRARY_PATH: `${root}:${environment.LD_LIBRARY_PATH ?? ''}`, E2E_CLOCK_REPLAY: clock,
      }, stdio: ['ignore', output, output],
    });
    closeSync(output);
    await expect.poll(async () => {
      expect(child?.exitCode, log()).toBeNull();
      return request.get('http://127.0.0.1:18780/readyz').then(response => response.status(), () => 0);
    }, { message: 'the signing-key node starts regardless of stored file contents' }).toBe(200);
  };
  const stop = async () => {
    if (child && child.exitCode === null) {
      child.kill('SIGKILL');
      await once(child, 'exit');
    }
    child = undefined;
  };
  const metrics = async () => (await request.get('http://127.0.0.1:19780/metrics')).text();
  const loaded = async () => {
    const value = await metrics();
    return Number(value.match(/^terrabase_auth_signing_keys_loaded(?:\{[^\n]*\})? (\d+)$/m)?.[1]);
  };
  const rejected = async (reason = 'invalid') => {
    const value = await metrics();
    return Number(value.match(new RegExp(`^terrabase_auth_stored_keys_rejected_total\\{[^\\n]*reason="${reason}"[^\\n]*\\} (\\d+)$`, 'm'))?.[1] ?? 0);
  };
  return {
    stored, start, stop, log, metrics, loaded, rejected, setClock,
    outage: (value: boolean) => { unavailable = value; },
    keyOutage: (value: boolean) => { keysUnavailable = value; },
    keys: (value: Record<string, unknown>[], only = false) => { if (only) replace = value; else additions = value; },
    fetches: () => fetched,
    exchange: (transform?: (value: string) => string) => { tokenTransform = transform; },
    hold: () => { holdNext = true; },
    release: () => { held?.(); held = undefined; },
    held: () => held !== undefined,
    finish: async () => {
      held?.();
      await stop();
      for (const tunnel of tunnels) tunnel.destroy();
      proxy.closeAllConnections();
      await new Promise<void>(resolve => proxy.close(() => resolve()));
      tls.closeAllConnections();
      tls.close();
      rmSync(root, { recursive: true, force: true });
    },
  };
}

export async function postgres(database: string, password: string) {
  const socket = connect(16780, '127.0.0.1');
  const frames: { type: string; body: Buffer }[] = [];
  let buffered = Buffer.alloc(0);
  let signal: (() => void) | undefined;
  let ended = false;
  socket.on('data', data => {
    buffered = Buffer.concat([buffered, Buffer.isBuffer(data) ? data : Buffer.from(data)]);
    while (buffered.length >= 5 && buffered.length >= buffered.readInt32BE(1) + 1) {
      const size = buffered.readInt32BE(1) + 1;
      frames.push({ type: buffered.toString('ascii', 0, 1), body: buffered.subarray(5, size) });
      buffered = buffered.subarray(size);
    }
    signal?.();
  });
  socket.on('close', () => { ended = true; signal?.(); });
  socket.on('error', () => { ended = true; signal?.(); });
  const number = (value: number) => { const bytes = Buffer.alloc(4); bytes.writeInt32BE(value); return bytes; };
  const text = (value: string) => Buffer.from(value + '\0');
  const send = (type: string, body = Buffer.alloc(0)) => socket.write(Buffer.concat([Buffer.from(type), number(body.length + 4), body]));
  const read = async (finish = 'Z') => {
    const errors: string[] = [];
    for (;;) {
      if (!frames.length) {
        if (ended) throw new Error('PostgreSQL connection ended before its response');
        await new Promise<void>(resolve => { signal = resolve; });
        signal = undefined;
        continue;
      }
      const frame = frames.shift()!;
      if (frame.type === 'E') {
        const fields = frame.body.toString().split('\0');
        errors.push(fields.find(value => value.startsWith('C'))?.slice(1) ?? 'missing-sqlstate');
        if (fields.includes('SFATAL')) return errors;
      }
      if (frame.type === finish) return errors;
    }
  };
  await once(socket, 'connect');
  const startup = Buffer.concat([number(196608), text('user'), text('any'), text('database'), text(database), Buffer.from([0])]);
  socket.write(Buffer.concat([number(startup.length + 4), startup]));
  send('p', text(password));
  const authenticated = await read();
  return {
    authenticated, ended: () => ended,
    query: async () => { send('Q', text('SELECT 1')); return read(); },
    prepare: async () => {
      send('P', Buffer.concat([text('probe'), text('SELECT 1'), Buffer.alloc(2)]));
      send('B', Buffer.concat([text('probe'), text('probe'), Buffer.alloc(6)]));
      send('D', Buffer.concat([Buffer.from('P'), text('probe')]));
      send('S');
      return read();
    },
    execute: async () => { send('E', Buffer.concat([text('probe'), number(0)])); send('S'); return read(); },
    close: () => socket.destroy(),
  };
}
