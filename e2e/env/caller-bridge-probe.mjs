import assert from 'node:assert/strict';
import { expect } from '@playwright/test';
import { readMetrics } from './caller-metrics.mjs';
import { h2 } from './caller-rpc.mjs';
import { randomUUID, sign } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { connect } from 'node:http2';
import { join } from 'node:path';
import { port as assigned } from './ports.mjs';

const [state, project, database] = process.argv.slice(2);
const authorization = process.env.E2E_CALLER_BEARER;
assert.ok(authorization);
const key = readFileSync(join(state, 'caller-bridge-key.pem'));
const peer = readFileSync(join(state, 'secrets'), 'utf8').split('\n')[0];
const path = '/v1/projects/' + project + '/databases/' + database;
const statement = JSON.stringify({ statements: [{ sql: 'SELECT value FROM caller_bridge_rows' }] });
const session = connect('http://127.0.0.1:' + assigned('internal'));
const metrics = port => readMetrics('http://127.0.0.1:' + port + '/metrics');
const count = (text, credential) => text.split('\n')
  .filter(line => line.startsWith('terrabase_internal_authentication_requests_total{') && line.includes('credential="' + credential + '"')
    && (credential !== 'caller' || line.includes('caller="journey-bridge-probe"')))
  .reduce((total, line) => total + Number(line.split(' ').at(-1)), 0);
async function requests() {
  const response = await fetch('http://127.0.0.1:' + assigned('terrabase') + path + '/analytics', { headers: { authorization } });
  assert.equal(response.status, 200);
  const result = await response.json();
  return result.months.reduce((total, month) => total + (month.metrics.requests ?? 0), 0);
}
function token() {
  const now = Math.floor(Date.now() / 1000);
  const header = Buffer.from(JSON.stringify({ alg: 'ES256', typ: 'ts-caller+jwt', kid: 'journey-bridge-probe.local.1' })).toString('base64url');
  const claims = Buffer.from(JSON.stringify({ iss: 'journey-bridge-probe', aud: 'terrabase', iat: now, exp: now + 240, jti: randomUUID() })).toString('base64url');
  const payload = header + '.' + claims;
  return payload + '.' + sign('sha256', Buffer.from(payload), { key, dsaEncoding: 'ieee-p1363' }).toString('base64url');
}
try {
  for (const caller of [false, true]) {
    const before = await requests();
    const sourceBefore = await metrics(assigned('metrics'));
    const response = await h2(session, path + '/sql/execute', {
      authorization, 'X-TerraBase-Peer-Credential': peer, ...(caller ? { 'X-TerraScale-Caller': token() } : {}),
    }, statement);
    assert.equal(response.status, 200, 'bearer internal call succeeds, caller=' + caller + ': ' + response.text);
    assert.deepEqual(JSON.parse(response.text).results[0].rows, [[291]]);
    await expect.poll(requests, { message: 'gru usage flush', intervals: [250] }).toBeGreaterThan(before);
    assert.equal(await requests() - before, 1, 'internal SQL metered once in gru');
    const sourceAfter = await metrics(assigned('metrics'));
    if (caller) assert.equal(count(sourceAfter, 'caller') - count(sourceBefore, 'caller'), 1);
    else assert.ok(count(sourceAfter, 'peer') > count(sourceBefore, 'peer'));
    assert.equal(count(sourceAfter, 'caller_rejected') - count(sourceBefore, 'caller_rejected'), 0);
    console.log('bridge bearer caller=' + caller + ': gru rows=[[291]], gru metered requests +1, outer authentication recorded, rejected caller +0');
  }
  const before = await metrics(assigned('metrics_fra'));
  let rows;
  await expect.poll(async () => {
    const response = await fetch('http://127.0.0.1:' + assigned('terrabase_fra') + path + '/sql/execute', {
      method: 'POST', headers: { authorization, 'content-type': 'application/json', 'X-TerraScale-Caller': 'junk' }, body: statement,
    });
    assert.equal(response.status, 200);
    rows = (await response.json()).results[0].rows;
    return rows;
  }, { message: 'fra converges on the gru rows', intervals: [250] }).toEqual([[291]]);
  assert.equal(count(await metrics(assigned('metrics_fra')), 'caller_rejected') - count(before, 'caller_rejected'), 0);
  console.log('public caller header on fra: fra rows=' + JSON.stringify(rows) + ', rejected caller +0');
} finally {
  session.close();
}
