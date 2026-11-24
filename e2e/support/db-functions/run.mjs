import assert from 'node:assert/strict';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import { sqlCases } from './cases.mjs';
import { advanced, binaryParameters, requestLimits, wireHardening } from './advanced.mjs';
import { token, transport } from './transports.mjs';

const surfaces = ['REST', 'WebSocket', 'Postgres'];
const normalize = rows => rows.map(row => row.map(value => value === null ? null : String(value)));

export async function runMatrix(state, target, selection) {
  const { createTerraBaseClient } = await import('../../../sdk/js/src/index.js');
  assert(['local', 'production'].includes(target));
  assert.match(state.projectId, /^[0-9A-HJKMNP-TV-Z]{26}$/);
  assert.match(state.databaseId, /^[0-9A-HJKMNP-TV-Z]{26}$/);
  const regions = target === 'local' ? ['gru', 'fra'] : ['gru', 'sjc', 'fra'];
  if (selection) assert(regions.includes(selection.region) && surfaces.includes(selection.surface));
  const ports = target === 'local' ? JSON.parse(readFileSync(resolve(process.env.E2E_STATE, 'ports.json'), 'utf8')) : undefined;
  const results = [];
  const record = (region, surface, name, status, detail = '') => {
    const cell = { target, region, surface, name, status, detail };
    results.push(cell);
    process.stdout.write(JSON.stringify(cell) + '\n');
  };
  const credential = { apiKey: state.apiKey, token: await token(state.apiKey, state.projectId) };
  const scoper = createTerraBaseClient({ apiKey: state.apiKey, project: state.projectId });
  const sdk = createTerraBaseClient({ token: credential.token, project: state.projectId });
  const readKey = await scoper.createScopedKey({ database: state.databaseId, access: 'read', ttlSeconds: 3600 });
  const read = { apiKey: readKey.key, token: await token(readKey.key, state.projectId) };
  if (target === 'local') {
    const directory = resolve(process.env.E2E_STATE, 'secrets.d');
    mkdirSync(directory, { recursive: true, mode: 0o700 });
    writeFileSync(resolve(directory, 'c35-' + state.databaseId), [credential.token, read.apiKey, read.token].join('\n'), { mode: 0o600 });
  }
  const check = async (region, surface, name, action) => {
    try {
      await action();
      record(region, surface, name, 'PASS');
      return true;
    } catch (error) {
      const code = error.code ?? error.sqlstate ?? error.name;
      record(region, surface, name, 'FAIL', String(code) + ': ' + error.message.slice(0, 400));
      return false;
    }
  };
  for (const region of regions) {
    for (const surface of surfaces) {
      if (selection && (region !== selection.region || surface !== selection.surface)) continue;
      const prefix = 'c35_' + region + '_' + surface.toLowerCase();
      const cases = sqlCases(prefix);
      let client;
      const connected = await check(region, surface, 'connect', async () => {
        client = await transport(surface, region, state.projectId, state.databaseId, credential, ports);
      });
      if (!connected) {
        for (const item of cases) record(region, surface, item.name, 'BLOCKED', 'connection failed');
        continue;
      }
      try {
        for (const item of cases) {
          let failure;
          let answer;
          try { answer = await client.execute([{ sql: item.sql, ...(item.args && { args: item.args }), ...(item.named && { named: item.named }) }]); }
          catch (error) { failure = error; }
          await check(region, surface, item.name, async () => {
            if (item.error) {
              assert(failure, 'expected refusal');
              assert.equal(failure.sqlstate, item.sqlstate);
              if (surface !== 'Postgres') assert.equal(failure.code, item.error);
            } else {
              if (failure) throw failure;
              if (item.rows) assert.deepEqual(normalize(answer[0].rows), item.rows);
            }
          });
        }
        const t = prefix + '_transactions';
        await check(region, surface, 'transaction-table', () => client.execute([{ sql: `CREATE TABLE ${t}(id INTEGER PRIMARY KEY)` }]));
        if (surface !== 'Postgres') {
          await check(region, surface, 'batch-commit', async () => {
            await client.execute([{ sql: `INSERT INTO ${t} VALUES(1)` }, { sql: `INSERT INTO ${t} VALUES(2)` }]);
            assert.deepEqual(normalize((await client.execute([{ sql: `SELECT id FROM ${t} ORDER BY id` }]))[0].rows), [['1'], ['2']]);
          });
          await check(region, surface, 'refused-request-rolls-back', async () => {
            await assert.rejects(client.execute([{ sql: `INSERT INTO ${t} VALUES(3)` }, { sql: "ATTACH DATABASE ':memory:' AS other" }]));
            assert.deepEqual(normalize((await client.execute([{ sql: `SELECT count(*) FROM ${t} WHERE id=3` }]))[0].rows), [['0']]);
          });
        }
        if (client.control) {
          for (const end of ['commit', 'rollback']) await check(region, surface, 'transaction-' + end, async () => {
            await client.control('begin');
            await client.execute([{ sql: `INSERT INTO ${t} VALUES(10)` }]);
            await client.control(end);
            assert.deepEqual(normalize((await client.execute([{ sql: `SELECT count(*) FROM ${t} WHERE id=10` }]))[0].rows), [[end === 'commit' ? '1' : '0']]);
            await client.execute([{ sql: `DELETE FROM ${t} WHERE id=10` }]);
          });
        }
        await check(region, surface, 'read-only-refusal', async () => {
          const readonly = await transport(surface, region, state.projectId, state.databaseId, read, ports);
          try {
            await readonly.execute([{ sql: 'SELECT 1' }]);
            await assert.rejects(readonly.execute([{ sql: `INSERT INTO ${t} VALUES(99)` }]), error => {
              assert.equal(error.sqlstate, '42501');
              if (surface !== 'Postgres') assert.equal(error.code, 'forbidden_scope');
              if (surface === 'REST') assert.equal(error.status, 403);
              return true;
            });
            assert.deepEqual(normalize((await client.execute([{ sql: `SELECT count(*) FROM ${t} WHERE id=99` }]))[0].rows), [['0']]);
          } finally { await readonly.close(); }
        });
        await advanced(client, surface, prefix, (name, action) => check(region, surface, name, action));
        if (surface === 'Postgres') {
          await binaryParameters(client, (name, action) => check(region, surface, name, action));
          await wireHardening(client, prefix, (name, action) => check(region, surface, name, action));
        }
        await requestLimits(() => transport(surface, region, state.projectId, state.databaseId, credential, ports),
          surface, (name, action) => check(region, surface, name, action));
      } finally { await client.close(); }
    }
  }
  if (!selection || (selection.region === 'gru' && selection.surface === 'REST')) {
    await check('gru', 'SDK', 'exact-int64-parameters', async () => {
    const db = sdk.database(state.databaseId);
    const result = await db.execute('SELECT ?,?', [9223372036854775807n, -9223372036854775808n]);
    assert.deepEqual(normalize(result.rows), [['9223372036854775807', '-9223372036854775808']]);
    });
    await check('gru', 'SDK', 'export-stream', async () => {
    const stream = await sdk.database(state.databaseId).exportDatabase();
    const dump = await new Response(stream).text();
    assert.match(dump, /BEGIN/);
    assert.match(dump, /CREATE TABLE/);
    assert.match(dump, /COMMIT/);
    });
  }
  process.stdout.write('| Target | Region | Surface | Pass | Fail | Blocked |\n| --- | --- | --- | ---: | ---: | ---: |\n');
  for (const region of regions) for (const surface of [...surfaces, 'SDK']) {
    const cells = results.filter(cell => cell.region === region && cell.surface === surface);
    if (cells.length) process.stdout.write(`| ${target} | ${region} | ${surface} | ${['PASS', 'FAIL', 'BLOCKED'].map(status => cells.filter(cell => cell.status === status).length).join(' | ')} |\n`);
  }
  return results;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const state = JSON.parse(readFileSync(0, 'utf8'));
    const selection = process.argv[3] ? { region: process.argv[3], surface: process.argv[4] } : undefined;
    const results = await runMatrix(state, process.argv[2], selection);
    process.exitCode = results.some(cell => cell.status === 'FAIL' || cell.status === 'BLOCKED') ? 1 : 0;
  } catch (error) {
    process.stderr.write('matrix stopped: ' + error.name + '\n');
    process.exitCode = 1;
  }
}
