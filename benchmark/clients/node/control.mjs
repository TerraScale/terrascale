import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { createSecureServer } from 'node:http2';
import { Agent, buildConnector, getGlobalDispatcher, setGlobalDispatcher } from 'undici';
import { Observer, setActive, tokenSource, cellRejection } from './observer.mjs';
import { createTerraBaseV1Client } from '@terrascale/sdk';

assert.notEqual(process.env.NODE_TLS_REJECT_UNAUTHORIZED, '0');
const run = promisify(execFile);
const directory = await mkdtemp(join(tmpdir(), 'node-parity-control-'));
const keyPath = join(directory, 'key.pem');
const certPath = join(directory, 'cert.pem');
const project = '01ARZ3NDEKTSV4RRFFQ69G5FA1';
const seed = { id: project, version: 'opaque', body: { payload: 'fixture' } };
const previous = getGlobalDispatcher();
try {
  await run('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1', '-keyout', keyPath,
    '-out', certPath, '-subj', '/CN=api.terrascale.tech', '-addext', 'subjectAltName=DNS:api.terrascale.tech,DNS:auth.terrascale.tech',
    '-addext', 'basicConstraints=critical,CA:TRUE']);
  const cert = await readFile(certPath);
  const tlsKey = await readFile(keyPath);
  for (const lane of ['raw-h1', 'sdk-h1', 'raw-h2', 'sdk-h2']) {
    for (const fallback of lane.endsWith('h2') ? [false, true] : [false]) {
      const protocol = lane.endsWith('h2') ? 'h2' : 'http/1.1';
      const served = fallback ? 'http/1.1' : protocol;
      const sockets = new Set();
      let duplicate = false, retry = false, stallAuth = false, authCalls = 0, dataCalls = 0;
      const server = createSecureServer({ key: tlsKey, cert, allowHTTP1: true,
        ALPNCallback: ({ protocols }) => protocols.includes(served) ? served : undefined });
      server.on('secureConnection', socket => { sockets.add(socket); socket.once('close', () => sockets.delete(socket)); });
      server.on('request', (request, response) => {
        const authority = request.headers[':authority'] ?? request.headers.host;
        if (authority === 'auth.terrascale.tech') {
          authCalls++;
          assert.equal(request.headers['x-terrascale-region-pin'], undefined);
          request.resume();
          if (stallAuth) return;
          response.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify({
            access_token: 'fixture.payload.signature', token_type: 'Bearer', expires_in: 3600, scope: 'documents:read' }));
          return;
        }
        dataCalls++;
        assert.equal(authority, 'api.terrascale.tech');
        assert.equal(request.headers['x-terrascale-region-pin'], 'gru');
        assert.equal(request.headers.authorization, 'Bearer fixture.payload.signature');
        if (retry) { retry = false; response.writeHead(503).end(); return; }
        response.writeHead(200, { 'content-type': 'application/json', 'x-terrabase-region': duplicate ? ['gru', 'gru'] : 'gru' })
          .end(JSON.stringify({ ...seed, state: 'value', document: seed.body }));
      });
      await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
      const connector = buildConnector({ ca: cert, allowH2: lane.endsWith('h2') });
      const dispatcher = new Agent({ allowH2: lane.endsWith('h2'), connect(options, callback) {
        assert.ok(['api.terrascale.tech', 'auth.terrascale.tech'].includes(options.hostname));
        connector({ ...options, hostname: '127.0.0.1', port: server.address().port, servername: options.hostname }, callback);
      } });
      setGlobalDispatcher(dispatcher);
      const docPath = `/v1/projects/${project}/databases/fixture/collections/bench/documents/by-key`;
      const key = `${project}_${'a'.repeat(52)}`;
      const observer = new Observer(new Set([`GET ${docPath}`]), 'gru', protocol === 'h2' ? 'HTTP/2' : 'HTTP/1.1');
      setActive(observer);
      try {
        const token = tokenSource(key, project);
        const collection = createTerraBaseV1Client({ project, token, baseUrl: 'https://api.terrascale.tech', fetch: observer.observe })
          .database('fixture').collection('bench');
        const signal = AbortSignal.timeout(20_000);
        const op = async () => {
          const body = lane.startsWith('sdk') ? await collection.get(seed.id, { signal })
            : await (await observer.observe(`https://api.terrascale.tech${docPath}?id=${seed.id}`,
              { headers: { authorization: `Bearer ${await token(signal)}` }, signal })).json();
          assert.equal(body.id, seed.id);
          assert.equal(body.version, seed.version);
          assert.equal(body.state, 'value');
          assert.deepEqual(body.document, seed.body);
        };
        await op();
        observer.window();
        await Promise.all(Array.from({ length: 16 }, () => op()));
        assert.equal(observer.attempts, 16);
        assert.equal(observer.good, 16);
        assert.equal(observer.sent, 16);
        assert.equal(observer.protocolBad, fallback ? 16 : 0);
        assert.equal(cellRejection(true, { started: 16, failed: 0, stuck: false, latencies: [1] }, observer), fallback ? 'attempt_mismatch' : null);
        const connections = observer.connects;
        for (let i = 0; i < 3; i++) await op();
        assert.equal(observer.connects, connections);

        assert.equal(authCalls, 1);
        duplicate = true;
        observer.window();
        await op();
        assert.equal(observer.attempts, 1);
        assert.equal(observer.good, 0);
        assert.equal(cellRejection(true, { started: 1, failed: 0, stuck: false, latencies: [1] }, observer), 'attempt_mismatch');
        duplicate = false;
        if (lane.startsWith('sdk')) {
          observer.window();
          retry = true;
          await op();
          assert.equal(observer.attempts, 2);
          assert.equal(observer.good, 1);
          assert.equal(observer.sent, 2);
          assert.equal(cellRejection(true, { started: 1, failed: 0, stuck: false, latencies: [1] }, observer), 'attempt_mismatch');
        }
        const ordinaryAuthCalls = authCalls;
        stallAuth = true;
        await assert.rejects(tokenSource(key, project)(AbortSignal.timeout(100)), error => error.name === 'TimeoutError');
        console.log(JSON.stringify({ lane, served, fallback, api_connections: connections,
          concurrent_attempts: 16, auth_calls: ordinaryAuthCalls, stalled_auth_cancelled: true, data_calls: dataCalls,
          duplicate_region_rejected: true, sdk_retry_observed: lane.startsWith('sdk'), passed: true }));
      } finally {
        setActive(null);
        setGlobalDispatcher(previous);
        await dispatcher.destroy();
        for (const socket of sockets) socket.destroy();
        await new Promise(resolve => server.close(resolve));
      }
    }
  }
} finally {
  setGlobalDispatcher(previous);
  await rm(directory, { recursive: true, force: true });
}
