import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { connect } from 'node:http2';
import { join } from 'node:path';
import { readMetrics } from './caller-metrics.mjs';
import { h2 } from './caller-rpc.mjs';
import { port } from './ports.mjs';

const state = process.argv[2];
const peer = readFileSync(join(state, 'secrets'), 'utf8').split('\n')[0];
const session = connect('http://127.0.0.1:' + port('internal'));
const metrics = 'http://127.0.0.1:' + port('metrics') + '/metrics';
const count = text => text.split('\n')
  .filter(line => line.startsWith('terrabase_internal_authentication_requests_total{') && line.includes('credential="peer"') && line.includes('caller="peer"'))
  .reduce((total, line) => total + Number(line.split(' ').at(-1)), 0);
const body = JSON.stringify({ statements: [{ sql: 'SELECT 29 AS caller_value' }] });
try {
  const before = await readMetrics(metrics);
  const result = await h2(session, '/v1/internal/databases/00000000000000000000000003/sql/execute',
    { 'X-TerraBase-Peer-Credential': peer }, body);
  assert.equal(result.status, 200, result.text);
  assert.deepEqual(JSON.parse(result.text).results[0].rows, [[29]]);
  assert.equal(count(await readMetrics(metrics)) - count(before), 1, 'internal peer SQL counted once');
  console.log('internal peer SQL counted once: HTTP/2 200 rows=[[29]] counted once before other services start');
} finally {
  session.close();
}
