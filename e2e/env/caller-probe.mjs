import assert from 'node:assert/strict';
import { generateKeyPairSync, randomUUID, sign } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { connect } from 'node:http2';
import { join } from 'node:path';

const state = process.argv[2];
const origin = process.argv[3] ?? 'http://127.0.0.1:8081';
const metrics = process.argv[4] ?? 'http://127.0.0.1:9091/metrics';
const key = readFileSync(join(state, 'caller-key.pem'));
const peer = readFileSync(join(state, 'secrets'), 'utf8').split('\n')[0];
const session = connect(origin);
const sql = '/v1/projects/00000000000000000000000002/databases/ts-auth/sql/execute';
const body = JSON.stringify({ statements: [{ sql: 'SELECT 29 AS caller_value' }] });
const peerHeader = { 'X-TerraBase-Peer-Credential': peer };
function token(kid = 'journey.local.1', age = 0, signingKey = key, extra = {}) {
  const now = Math.floor(Date.now() / 1000) + age;
  const header = Buffer.from(JSON.stringify({ alg: 'ES256', typ: 'ts-caller+jwt', kid })).toString('base64url');
  const claims = Buffer.from(JSON.stringify({ iss: kid.startsWith('op.') ? 'op:journey' : kid.startsWith('journey-catalog.') ? 'journey-catalog' : 'journey', aud: 'terrabase', iat: now, exp: now + 240, jti: randomUUID(), ...extra })).toString('base64url');
  const payload = header + '.' + claims;
  return payload + '.' + sign('sha256', Buffer.from(payload), { key: signingKey, dsaEncoding: 'ieee-p1363' }).toString('base64url');
}
async function request(path, headers, content = body) {
  return new Promise((resolve, reject) => {
    const call = session.request({ ':method': content === null ? 'GET' : 'POST', ':path': path, 'content-type': 'application/json', ...headers });
    let status;
    let text = '';
    call.on('response', headers => { status = headers[':status']; });
    call.setEncoding('utf8');
    call.on('data', chunk => { text += chunk; });
    call.on('error', reject);
    call.on('end', () => resolve({ status, text }));
    call.end(content);
  });
}
function count(text, name, labels) {
  return text.split('\n').filter(line => line.startsWith(name + '{') && labels.every(label => line.includes(label)))
    .reduce((sum, line) => sum + Number(line.split(' ').at(-1)), 0);
}
try {
  const before = await (await fetch(metrics)).text();
  const caller = { 'X-TerraScale-Caller': token() };
  const own = await request(sql, caller);
  assert.equal(own.status, 200, own.text);
  assert.deepEqual(JSON.parse(own.text).results[0].rows, [[29]]);
  console.log('valid caller own project: 200 rows=[[29]]');
  for (const [label, path, headers, expected] of [
    ['operator read capability', sql, { 'X-TerraScale-Caller': token('op.journey.1', 0, key, { cap: ['platform.read'], reason: 'journey' }) }, 200],
    ['operator capability escalation', sql, { 'X-TerraScale-Caller': token('op.journey.1', 0, key, { cap: ['admin'], reason: 'journey' }) }, 401],
    ['operator missing reason', sql, { 'X-TerraScale-Caller': token('op.journey.1', 0, key, { cap: ['platform.read'] }) }, 401],
    ['other project', sql.replace('00000000000000000000000002', '00000000000000000000000005').replace('ts-auth', 'ts-dashboard'), caller, 403],
    ['admin route', '/admin/api-keys', caller, 403],
    ['valid caller with peer still scoped', '/admin/api-keys', { ...caller, ...peerHeader }, 403],
    ['peer-only SQL', sql, peerHeader, 200],
    ['unknown kid with peer', sql, { 'X-TerraScale-Caller': token('unknown.local.1'), ...peerHeader }, 200],
    ['unknown kid alone', sql, { 'X-TerraScale-Caller': token('unknown.local.1') }, 401],
    ['expired with peer', sql, { 'X-TerraScale-Caller': token('journey.local.1', -600), ...peerHeader }, 200],
    ['service user header', sql, { ...caller, 'X-TerraBase-User-Id': '00000000000000000000000009' }, 403],
    ['caller forwarding with peer', sql, { ...caller, ...peerHeader, 'X-TerraBase-Forwarded-For': '127.0.0.1' }, 403],
    ['expired alone', sql, { 'X-TerraScale-Caller': token('journey.local.1', -600) }, 401],
    ['mis-signed alone', sql, { 'X-TerraScale-Caller': token('journey.local.1', 0, generateKeyPairSync('ec', { namedCurve: 'prime256v1' }).privateKey) }, 401],
    ['caller on peer route', '/v1/sync/organizations/00000000000000000000000001/projects/00000000000000000000000002/databases/00000000000000000000000003/pull-updates', caller, 403],
  ]) {
    const result = await request(path, headers);
    assert.equal(result.status, expected, label + ': ' + result.text);
    console.log(label + ': ' + result.status);
  }
  const operator = { 'X-TerraScale-Caller': token('op.journey.1', 0, key, { cap: ['platform.read'], reason: 'journey' }) };
  const write = await request(sql, operator, JSON.stringify({ statements: [{ sql: 'CREATE TABLE caller_proof_write (id INTEGER)' }] }));
  assert.equal(write.status, 403, write.text);
  console.log('operator read capability rejects SQL writes: 403');
  for (const [label, jwt, expected, path] of [
    ['gRPC caller denied by replay grants', token(), '7', '/IPlatformService/ListDatabasesAsync'],
    ['gRPC public discovery with caller', token(), '0', '/IRegionsService/ListAsync'],
    ['gRPC catalog caller', token('journey-catalog.local.1'), '0', '/IPlatformService/ListDatabasesAsync'],
  ]) {
    const result = await new Promise((resolve, reject) => {
      const call = session.request({
        ':method': 'POST', ':path': path,
        'content-type': 'application/grpc', te: 'trailers', 'X-TerraScale-Caller': jwt,
      });
      let status;
      let grpc;
      const chunks = [];
      call.on('response', headers => { status = headers[':status']; grpc = headers['grpc-status']; });
      call.on('trailers', headers => { grpc = headers['grpc-status']; });
      call.on('data', chunk => { chunks.push(chunk); });
      call.on('error', reject);
      call.on('end', () => resolve({ status, grpc, body: Buffer.concat(chunks) }));
      call.end(Buffer.from([0, 0, 0, 0, 1, 0xc0]));
    });
    assert.equal(result.status, 200, label);
    assert.equal(result.grpc, expected, label);
    if (label === 'gRPC catalog caller') assert.ok(result.body.includes(Buffer.from('ts-auth')), 'gRPC catalog has platform data');
    console.log(label + ': HTTP ' + result.status + ' grpc-status=' + result.grpc);
  }
  const acting = await request('/v1/management/organizations',
    { ...peerHeader, 'X-TerraBase-User-Id': '00000000000000000000000009' }, null);
  assert.equal(acting.status, 200, acting.text);
  console.log('peer-only act-as: 200');
  const after = await (await fetch(metrics)).text();
  for (const [name, labels] of [
    ['terrabase_internal_authentication_requests_total', ['credential="caller"', 'caller="journey"']],
    ['terrabase_internal_authentication_requests_total', ['credential="legacy"']],
    ['terrabase_internal_authentication_requests_total', ['credential="caller_rejected"']],
    ['terrabase_operator_legacy_grants_total', ['kind="data"']],
    ['terrabase_operator_legacy_grants_total', ['kind="act_as"']],
  ]) {
    assert.ok(count(after, name, labels) > count(before, name, labels), name + labels.join(','));
    console.log(name + '{' + labels.join(',') + '}: increased');
  }
} finally {
  session.close();
}
