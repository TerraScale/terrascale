import assert from 'node:assert/strict';
import { readMetrics } from './caller-metrics.mjs';
import { randomUUID, sign } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { connect } from 'node:http2';
import { join } from 'node:path';

const [state, project, database] = process.argv.slice(2);
const authorization = process.env.E2E_CALLER_BEARER;
assert.ok(authorization);
const key = readFileSync(join(state, 'caller-key.pem'));
const peer = readFileSync(join(state, 'secrets'), 'utf8').split('\n')[0];
const path = '/v1/projects/' + project + '/databases/' + database;
const session = connect('http://127.0.0.1:8081');
const metrics = port => readMetrics('http://127.0.0.1:' + port + '/metrics');
const count = (text, credential) => text.split('\n')
  .filter(line => line.startsWith('terrabase_internal_authentication_requests_total{') && line.includes('credential="' + credential + '"'))
  .reduce((total, line) => total + Number(line.split(' ').at(-1)), 0);
async function posts() {
  const response = await fetch('http://127.0.0.1:8180' + path + '/analytics?region=fra', { headers: { authorization } });
  assert.equal(response.status, 200);
  const result = await response.json();
  return result.items.flatMap(item => item.series).reduce((total, point) => total + (point.requests.post ?? 0), 0);
}
function token() {
  const now = Math.floor(Date.now() / 1000);
  const header = Buffer.from(JSON.stringify({ alg: 'ES256', typ: 'ts-caller+jwt', kid: 'dashboard.local.1' })).toString('base64url');
  const claims = Buffer.from(JSON.stringify({ iss: 'dashboard', aud: 'terrabase', iat: now, exp: now + 240, jti: randomUUID() })).toString('base64url');
  const payload = header + '.' + claims;
  return payload + '.' + sign('sha256', Buffer.from(payload), { key, dsaEncoding: 'ieee-p1363' }).toString('base64url');
}
function string(value) {
  const bytes = Buffer.from(value);
  assert.ok(bytes.length < 256);
  return Buffer.concat([Buffer.from([0xd9, bytes.length]), bytes]);
}
async function sql(caller) {
  const json = Buffer.from(JSON.stringify({ statements: [{ sql: 'SELECT value FROM caller_bridge_rows' }] }));
  const message = Buffer.concat([Buffer.from([0x93]), string(project), string(database), Buffer.from([0xc4, json.length]), json]);
  const frame = Buffer.alloc(5);
  frame.writeUInt32BE(message.length, 1);
  return new Promise((resolve, reject) => {
    const call = session.request({
      ':method': 'POST', ':path': '/IManagementService/ExecuteSqlAsync',
      'content-type': 'application/grpc', te: 'trailers', authorization,
      'X-TerraBase-Peer-Credential': peer, ...(caller ? { 'X-TerraScale-Caller': token() } : {}),
    });
    let grpc;
    const chunks = [];
    call.on('response', headers => { grpc = headers['grpc-status']; });
    call.on('trailers', headers => { grpc = headers['grpc-status']; });
    call.on('data', chunk => { chunks.push(chunk); });
    call.on('error', reject);
    call.on('end', () => resolve({ grpc, body: Buffer.concat(chunks) }));
    call.end(Buffer.concat([frame, message]));
  });
}
try {
  for (const caller of [false, true]) {
    const before = await posts();
    const sourceBefore = await metrics(9091);
    const destinationBefore = await metrics(9191);
    const response = await sql(caller);
    assert.equal(response.grpc, '0', 'bearer bridge call succeeds, caller=' + caller);
    const packed = response.body.subarray(5);
    const offset = packed[0] === 0xc4 ? 2 : packed[0] === 0xc5 ? 3 : 5;
    assert.deepEqual(JSON.parse(packed.subarray(offset).toString()).results[0].rows, [[291]]);
    assert.equal(await posts() - before, 1, 'forwarded SQL metered in fra');
    const sourceAfter = await metrics(9091);
    const destinationAfter = await metrics(9191);
    if (caller) assert.equal(count(sourceAfter, 'caller') - count(sourceBefore, 'caller'), 1);
    else assert.ok(count(sourceAfter, 'peer') > count(sourceBefore, 'peer'));
    assert.equal(count(destinationAfter, 'caller_rejected') - count(destinationBefore, 'caller_rejected'), 0);
    console.log('bridge bearer caller=' + caller + ': fra rows=[[291]], fra metered POST +1, outer authentication recorded, rejected caller +0');
  }
  const before = await metrics(9191);
  const response = await fetch('http://127.0.0.1:8080' + path + '/sql/execute', {
    method: 'POST', headers: { authorization, 'content-type': 'application/json', 'X-TerraScale-Caller': 'junk' },
    body: JSON.stringify({ statements: [{ sql: 'SELECT value FROM caller_bridge_rows' }] }),
  });
  assert.equal(response.status, 200);
  assert.deepEqual((await response.json()).results[0].rows, [[291]]);
  assert.equal(count(await metrics(9191), 'caller_rejected') - count(before, 'caller_rejected'), 0);
  console.log('forwarded public caller header: fra rows=[[291]], rejected caller +0');
} finally {
  session.close();
}
