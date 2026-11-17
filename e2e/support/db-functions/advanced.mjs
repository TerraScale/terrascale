import assert from 'node:assert/strict';

const rows = result => result[0].rows.map(row => row.map(value => value === null ? null : String(value)));
const refused = (action, sqlstate) => assert.rejects(action, error => {
  assert.equal(error.sqlstate, sqlstate, 'observed ' + (error.code ?? error.name) + ': ' + error.message);
  return true;
});

export async function advanced(client, surface, prefix, check) {
  const t = prefix + '_transactions';
  const execute = (sql, args) => client.execute([{ sql, ...(args && { args }) }]);
  await check('foreign-key-delete-cascade', async () => {
    await execute(`CREATE TABLE ${prefix}_cascade(parent INTEGER REFERENCES ${t}(id) ON DELETE CASCADE)`);
    await execute(`INSERT INTO ${t} VALUES(90)`);
    await execute(`INSERT INTO ${prefix}_cascade VALUES(90)`);
    await execute(`DELETE FROM ${t} WHERE id=90`);
    assert.deepEqual(rows(await execute(`SELECT count(*) FROM ${prefix}_cascade`)), [['0']]);
  });
  await check('schema-pragmas', async () => {
    await execute(`CREATE UNIQUE INDEX ${prefix}_metadata_index ON ${t}(id)`);
    for (const pragma of ['table_info', 'table_xinfo']) {
      const result = rows(await execute(`PRAGMA ${pragma}(${t})`));
      assert.equal(result[0][1], 'id');
      assert.equal(result[0][5], '1');
    }
    assert(rows(await execute('PRAGMA table_list')).some(row => row[1] === t));
    assert(rows(await execute(`PRAGMA index_list(${t})`)).some(row => row[1] === prefix + '_metadata_index'));
    for (const pragma of ['index_info', 'index_xinfo']) assert(rows(await execute(`PRAGMA ${pragma}(${prefix}_metadata_index)`)).some(row => row[2] === 'id'));
    assert.equal(rows(await execute(`PRAGMA foreign_key_list(${prefix}_cascade)`))[0][6], 'CASCADE');
    assert.deepEqual(rows(await execute(`PRAGMA foreign_key_check(${prefix}_cascade)`)), []);
    assert.deepEqual(rows(await execute('PRAGMA foreign_keys')), [['1']]);
  });
  await check('stored-large-row', async () => {
    await execute(`CREATE TABLE ${prefix}_large(id INTEGER PRIMARY KEY,payload TEXT)`);
    const payload = 'abcd'.repeat(131072);
    await execute(`INSERT INTO ${prefix}_large VALUES(1,?)`, [payload]);
    assert.deepEqual(rows(await execute(`SELECT payload FROM ${prefix}_large`)), [[payload]]);
  });
  await check('transaction-write-limits', async () => {
    const table = prefix + '_limits';
    await execute(`CREATE TABLE ${table}(id INTEGER PRIMARY KEY,payload BLOB)`);
    const insert = n => execute(`WITH RECURSIVE r(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM r WHERE x<${n}) INSERT INTO ${table}(id) SELECT x FROM r`);
    await insert(1024);
    assert.deepEqual(rows(await execute(`SELECT count(*) FROM ${table}`)), [['1024']]);
    await execute(`DELETE FROM ${table}`);
    await refused(insert(1025), '54000');
    assert.deepEqual(rows(await execute(`SELECT count(*) FROM ${table}`)), [['0']]);
    await refused(execute(`INSERT INTO ${table} VALUES(1,zeroblob(8388609))`), '54000');
    assert.deepEqual(rows(await execute(`SELECT count(*) FROM ${table}`)), [['0']]);
  });
  await check('result-row-boundary', async () => {
    const result = rows(await execute('WITH RECURSIVE r(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM r WHERE x<10000) SELECT x FROM r'));
    assert.equal(result.length, 10000);
    assert.deepEqual(result[0], ['1']);
    assert.deepEqual(result.at(-1), ['10000']);
  });
  for (const [name, sql, state] of [
    ['sql-length-limit', "SELECT '" + 'x'.repeat(16376) + "'", '0A000'],
    ['result-byte-limit', 'SELECT hex(zeroblob(4194305))', '54000'],
    ['result-row-limit', 'WITH RECURSIVE r(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM r WHERE x<10001) SELECT x FROM r', '54000'],
    ['attach-refusal', "ATTACH DATABASE ':memory:' AS extra", '42501'],
    ['extension-refusal', "SELECT load_extension('missing')", '42501'],
    ['file-read-refusal', "SELECT readfile('/etc/passwd')", '42501'],
    ['file-write-refusal', "SELECT writefile('/tmp/c35-denied','no')", '42501'],
    ['internal-table-refusal', 'SELECT * FROM sqlite_dbpage', '42501'],
    ['detach-refusal', 'DETACH DATABASE main', '42501'],
    ['vacuum-refusal', 'VACUUM', '42501'],
    ['analyze-refusal', 'ANALYZE', '42501'],
    ['temporary-table-refusal', 'CREATE TEMP TABLE c35_temp(value INTEGER)', '42501'],
    ['virtual-table-refusal', 'CREATE VIRTUAL TABLE c35_virtual USING fts5(value)', '42501'],
    ['pragma-write-refusal', 'PRAGMA journal_mode=WAL', '42501'],
    ['alter-view-refusal', 'ALTER VIEW missing RENAME TO other', '42601'],
    ['alter-index-refusal', 'ALTER INDEX missing RENAME TO other', '42601'],
    ['alter-trigger-refusal', 'ALTER TRIGGER missing RENAME TO other', '42601'],
  ]) await check(name, () => refused(execute(sql), state));
  await check('sql-length-boundary', async () => {
    const value = 'x'.repeat(16375);
    assert.deepEqual(rows(await execute("SELECT '" + value + "'")), [[value]]);
  });
  if (surface !== 'Postgres') {
    await check('batch-256-boundary', async () => {
      const result = await client.execute(Array.from({ length: 256 }, () => ({ sql: 'SELECT 1' })));
      assert.equal(result.length, 256);
      assert(result.every(item => String(item.rows[0][0]) === '1'));
    });
    await check('batch-257-refusal', () => refused(client.execute(Array.from({ length: 257 }, () => ({ sql: 'SELECT 1' }))), '22023'));
  }
  if (client.control) {
    if (surface === 'Postgres') {
      await check('catalog-columns', async () => {
        const listed = rows(await execute(`SELECT c.oid, n.nspname, c.relname FROM pg_catalog.pg_class c WHERE c.relname = '${t}'`));
        const table = listed.find(row => row[2] === t);
        assert(table);
        const columns = rows(await execute(`SELECT a.attname, pg_catalog.format_type(a.atttypid,a.atttypmod) FROM pg_catalog.pg_attribute a WHERE a.attrelid = '${table[0]}'`));
        assert.deepEqual(columns.map(row => row.slice(0, 2)), [['id', 'integer']]);
      });
      await check('information-schema-refusal', () => refused(execute(`SELECT column_name FROM information_schema.columns WHERE table_name = '${t}'`), '0A000'));
      await check('catalog-uncommitted-schema-and-rollback', async () => {
        const table = prefix + '_catalog';
        const catalog = () => execute(`SELECT c.oid, n.nspname, c.relname FROM pg_catalog.pg_class c WHERE c.relname = '${table}'`);
        await client.control('begin');
        try {
          await execute(`CREATE TABLE ${table}(payload BLOB)`);
          assert(rows(await catalog()).some(row => row[2] === table));
        } finally { await client.control('rollback'); }
        assert(!rows(await catalog()).some(row => row[2] === table));
      });
      await check('describe-statement-before-binary-bind-and-reuse', async () => {
        const table = prefix + '_describe';
        await execute(`CREATE TABLE ${table}(payload BLOB)`);
        const sql = `INSERT INTO ${table}(payload) VALUES($1)`;
        for (const bytes of [Buffer.from([0, 255]), Buffer.from([1, 254])]) {
          const answer = await client.describeBind('c35_described_blob', sql, [bytes]);
          assert.deepEqual(answer.oids, [17]);
        }
        assert.deepEqual(rows(await execute(`SELECT hex(payload) FROM ${table} ORDER BY rowid`)), [['00FF'], ['01FE']]);
      });
      await check('trigger-quoted-semicolons-comments-and-trailing-statements', async () => {
        const source = prefix + '_trigger_source';
        const audit = prefix + '_trigger_strings';
        await execute(`CREATE TABLE ${source}(id INTEGER PRIMARY KEY)`);
        await execute(`CREATE TABLE ${audit}(value TEXT)`);
        const blockComment = String.fromCharCode(47, 42) + ' ; ' + String.fromCharCode(42, 47);
        const lineComment = String.fromCharCode(45, 45) + ' ;\n';
        await execute(`CREATE TRIGGER ${prefix}_strings AFTER INSERT ON ${source} BEGIN INSERT INTO ${audit} VALUES('x;y'); ${blockComment} ${lineComment} INSERT INTO ${audit} VALUES('a'';b'); END; INSERT INTO ${source} VALUES(1); INSERT INTO ${source} VALUES(2)`);
        assert.deepEqual(rows(await execute(`SELECT value FROM ${audit} ORDER BY rowid`)), [['x;y'], ["a';b"], ['x;y'], ["a';b"]]);
        assert.deepEqual(rows(await execute(`SELECT id FROM ${source} ORDER BY id`)), [['1'], ['2']]);
      });
      await check('parameter-metadata-latency', async () => {
        await execute(Array.from({ length: 200 }, (_, i) => `CREATE TABLE ${prefix}_latency_${i}(value INTEGER)`).join(';'));
        await execute('SET terrascale.write=home');
        try {
          for (let sample = 0; sample < 3; sample++) {
            await client.control('begin');
            try {
              await execute(`SELECT count(*) FROM ${t}`);
              const started = performance.now();
              for (let i = 0; i < 10; i++) assert.deepEqual(rows(await execute('SELECT ?', [i])), [[String(i)]]);
              process.stdout.write(JSON.stringify({ surface, stage: 'parameter-metadata-latency', tables: 200, sample, millisecondsPerQuery: (performance.now() - started) / 10 }) + '\n');
            } finally { await client.control('rollback'); }
          }
        } finally { await execute('SET terrascale.write=auto'); }
      });
      await check('metadata-does-not-promote-transaction', async () => {
        await client.control('begin');
        await execute(`INSERT INTO ${t} VALUES(?)`, [21]);
        await client.control('rollback');
        assert.deepEqual(rows(await execute(`SELECT count(*) FROM ${t} WHERE id=21`)), [['0']]);
      });
      await check('metadata-sees-uncommitted-schema', async () => {
        await client.control('begin');
        await execute(`CREATE TABLE ${prefix}_uncommitted(value BLOB)`);
        await execute(`INSERT INTO ${prefix}_uncommitted(value) VALUES(?)`, [{ base64: 'AP8=' }]);
        assert.deepEqual(rows(await execute(`SELECT hex(value) FROM ${prefix}_uncommitted`)), [['00FF']]);
        await client.control('rollback');
      });
    }
      await check('savepoint-rollback-release', async () => {
        await client.control('begin');
        await execute(`INSERT INTO ${t} VALUES(30)`);
        await execute('SAVEPOINT c35_save');
        await execute(`INSERT INTO ${t} VALUES(31)`);
        await execute('ROLLBACK TO c35_save');
        await execute('RELEASE c35_save');
        await client.control('commit');
        assert.deepEqual(rows(await execute(`SELECT id FROM ${t} WHERE id>=30 ORDER BY id`)), [['30']]);
      });
      await check('aborted-transaction', async () => {
        await client.control('begin');
        await execute(`INSERT INTO ${t} VALUES(40)`);
        await refused(execute(`INSERT INTO ${t} VALUES(40)`), '23505');
        await refused(execute('SELECT 1'), '25P02');
        await client.control('rollback');
        assert.deepEqual(rows(await execute(`SELECT count(*) FROM ${t} WHERE id=40`)), [['0']]);
      });
    if (surface === 'Postgres') {
      await check('simple-script-atomicity', async () => {
        await refused(execute(`INSERT INTO ${t} VALUES(50); ATTACH DATABASE ':memory:' AS other`), '42501');
        assert.deepEqual(rows(await execute(`SELECT count(*) FROM ${t} WHERE id=50`)), [['0']]);
      });
    }
  }
  await check('work-budget', () => refused(execute('WITH RECURSIVE r(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM r WHERE x<1000000000) SELECT SUM(x) FROM r'), '57014'));
  await check('work-budget-recovery', async () => { assert.deepEqual(rows(await execute('SELECT 1')), [['1']]); });
}

export async function binaryParameters(client, check) {
  const numeric = Buffer.from('0001000000000000002a', 'hex');
  const cases = [
    ['bool', 16, Buffer.from([1]), '1'],
    ['bytea', 17, Buffer.from([0, 255]), '00FF', 'hex(?)'],
    ['int2', 21, Buffer.from('8000', 'hex'), '-32768'],
    ['int4', 23, Buffer.from('7fffffff', 'hex'), '2147483647'],
    ['int8', 20, Buffer.from('7fffffffffffffff', 'hex'), '9223372036854775807'],
    ['float4', 700, Buffer.from('3fa00000', 'hex'), '1.25'],
    ['float8', 701, Buffer.from('3ff4000000000000', 'hex'), '1.25'],
    ['text', 25, Buffer.from('héllo'), 'héllo'],
    ['varchar', 1043, Buffer.from('text'), 'text'],
    ['bpchar', 1042, Buffer.from('a '), 'a '],
    ['json', 114, Buffer.from('{"n":1}'), '{"n":1}'],
    ['jsonb', 3802, Buffer.concat([Buffer.from([1]), Buffer.from('{"n":1}')]), '{"n":1}'],
    ['numeric', 1700, numeric, '42'],
    ['uuid', 2950, Buffer.from('00112233445566778899aabbccddeeff', 'hex'), '00112233-4455-6677-8899-aabbccddeeff'],
    ['date', 1082, Buffer.from('00000001', 'hex'), '2000-01-02'],
    ['timestamp', 1114, Buffer.from('0000000000000001', 'hex'), '2000-01-01 00:00:00.000001'],
    ['timestamptz', 1184, Buffer.from('0000000000000001', 'hex'), '2000-01-01 00:00:00.000001+00'],
  ];
  for (const [name, oid, bytes, expected, expression = '?'] of cases) await check('binary-' + name, async () => {
    const result = await client.execute([{ sql: 'SELECT ' + expression, args: [{ base64: bytes.toString('base64') }], oids: [oid] }]);
    assert.deepEqual(rows(result), [[expected]]);
  });
  for (const oid of [1082, 1114, 1184, 2950]) await check('binary-invalid-' + oid,
    () => refused(client.execute([{ sql: 'SELECT ?', args: [{ base64: 'AA==' }], oids: [oid] }]), '22P02'));
}

export async function requestLimits(open, surface, check) {
  const sql = 'SELECT length(?)';
  const statements = [{ sql, args: [''] }];
  const envelope = surface === 'WebSocket' ? { id: 2, type: 'execute', statements } : { statements };
  const overhead = surface === 'Postgres' ? 20 : Buffer.byteLength(JSON.stringify(envelope));
  const maximum = 1024 * 1024 - overhead;
  for (const extra of [0, 1]) await check(extra ? 'request-over-limit' : 'request-size-boundary', async () => {
    const client = await open();
    let observed;
    const validate = error => {
      if (surface === 'REST') assert.equal(error.status, 413);
      if (surface === 'WebSocket') assert.equal(error.closeCode, 1009);
      if (surface === 'Postgres') assert.equal(error.sqlstate, '08P01');
      observed = error;
      return true;
    };
    try {
      const call = client.execute([{ sql, args: ['x'.repeat(maximum + extra)] }]);
      if (extra) await assert.rejects(call, validate);
      else assert.deepEqual(rows(await call), [[String(maximum)]]);
    } finally {
      try { await client.close(); }
      catch (error) {
        assert(observed, 'unexpected connection failure during cleanup');
        validate(error);
      }
    }
  });
}
