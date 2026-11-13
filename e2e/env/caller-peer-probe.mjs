import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { connect } from 'node:http2';
import { join } from 'node:path';
import { readMetrics } from './caller-metrics.mjs';
import { rpc } from './caller-rpc.mjs';

const state = process.argv[2];
const peer = readFileSync(join(state, 'secrets'), 'utf8').split('\n')[0];
const session = connect('http://127.0.0.1:8081');
const metrics = 'http://127.0.0.1:9091/metrics';
const count = text => text.split('\n')
  .filter(line => line.startsWith('terrabase_internal_authentication_requests_total{') && line.includes('credential="peer"') && line.includes('caller="peer"'))
  .reduce((total, line) => total + Number(line.split(' ').at(-1)), 0);
const body = Buffer.from(JSON.stringify({ statements: [{ sql: 'SELECT 29 AS caller_value' }] }));
const message = Buffer.concat([Buffer.from([0x93, 0xba]), Buffer.from('00000000000000000000000002'),
  Buffer.from([0xa7]), Buffer.from('ts-auth'), Buffer.from([0xc4, body.length]), body]);
const frame = Buffer.alloc(5);
frame.writeUInt32BE(message.length, 1);
try {
  const before = await readMetrics(metrics);
  const result = await rpc(session, '/IManagementService/ExecuteSqlAsync',
    { 'X-TerraBase-Peer-Credential': peer }, Buffer.concat([frame, message]));
  assert.equal(result.status, 200);
  assert.equal(result.grpc, '0');
  const packed = result.body.subarray(5);
  const offset = packed[0] === 0xc4 ? 2 : packed[0] === 0xc5 ? 3 : 5;
  assert.deepEqual(JSON.parse(packed.subarray(offset).toString()).results[0].rows, [[29]]);
  assert.equal(count(await readMetrics(metrics)) - count(before), 1, 'gRPC peer SQL counted once');
  console.log('gRPC peer SQL counted once: HTTP 200 grpc-status=0 rows=[[29]] counted once before other services start');
} finally {
  session.close();
}
