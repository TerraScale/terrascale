import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { PrismaPg } from '@prisma/adapter-pg';
import { generateDrizzleJson, generateMigration } from 'drizzle-kit/api';
import { eq, sql } from 'drizzle-orm';
import { drizzle } from 'drizzle-orm/node-postgres';
import { index, jsonb, pgTable, text } from 'drizzle-orm/pg-core';
import pg from 'pg';
import { ulid } from 'ulid';

const root = dirname(fileURLToPath(import.meta.url)), cli = promisify(execFile);
const checks = ['connect', 'migrate_table', 'migrate_fk', 'migrate_index', 'introspect', 'crud', 'transactions', 'json', 'prepared', 'errors'];
const { DATABASE_URL, MATRIX_SURFACE: surface, MATRIX_REPEAT = '3' } = process.env;
if (!DATABASE_URL || !surface || !/^[1-9]\d*$/.test(MATRIX_REPEAT)) throw new Error('Required: DATABASE_URL, MATRIX_SURFACE, positive MATRIX_REPEAT');
const repeats = Number(MATRIX_REPEAT), config = { connectionString: DATABASE_URL, max: 1, connectionTimeoutMillis: 5000, query_timeout: 15000, statement_timeout: 15000 };
assert(Number.isSafeInteger(repeats));
const protocolDelta = (before, after) => ({ parse: after.parse - before.parse, bind: after.bind - before.bind });
const codes = (error, seen = new Set()) => {
  if (!error || typeof error !== 'object' || seen.has(error)) return [];
  seen.add(error);
  return [...new Set(Object.getOwnPropertyNames(error).flatMap(key => /^(code|originalCode|sqlState|sqlstate)$/.test(key) && /^[A-Z0-9_]{3,40}$/.test(String(error[key])) ? [String(error[key])] : codes(error[key], seen)))];
};
const runCli = async (tool, args, cwd) => {
  try { return (await cli(process.execPath, [join(root, 'node_modules', tool === 'prisma' ? 'prisma/build/index.js' : 'drizzle-kit/bin.cjs'), ...args], { cwd, timeout: 60000, maxBuffer: 8 * 1024 * 1024, env: { ...process.env, NO_COLOR: '1', DEBUG: '' } })).stdout; }
  catch (error) { error.matrix = { tool, command: args.slice(0, 2), exitCode: typeof error.code === 'number' ? error.code : null }; throw error; }
};
const expectState = async (operation, state, wireErrors) => {
  const start = wireErrors.length;
  try { await operation(); } catch (error) { assert([...codes(error), ...wireErrors.slice(start)].includes(state)); return state; }
  assert.fail(`Expected SQLSTATE ${state}; operation succeeded`);
};

async function prismaApi(dir, names, pool) {
  const schema = stage => `generator client {\n provider = "prisma-client-js"\n output = "./client"\n}\ndatasource db {\n provider = "postgresql"\n}\nmodel Parent {\n id String @id\n label String\n payload Json\n ${stage >= 1 ? 'children Child[]' : ''}\n ${stage >= 2 ? '@@index([label])' : ''}\n @@map("${names.parent}")\n}\nmodel Child {\n id String @id\n parentId String\n ${stage >= 1 ? 'parent Parent @relation(fields: [parentId], references: [id])' : ''}\n @@map("${names.child}")\n}\n`;
  for (let stage = 0; stage < 3; stage++) await writeFile(join(dir, `s${stage}.prisma`), schema(stage));
  await writeFile(join(dir, 'prisma.config.ts'), 'export default { datasource: { url: process.env.DATABASE_URL } };');
  await runCli('prisma', ['generate', '--schema', 's2.prisma'], dir);
  const { PrismaClient } = await import(join(dir, 'client/index.js'));
  const client = new PrismaClient({ adapter: new PrismaPg(pool), log: [] });
  const wrap = db => ({
    create: data => db.parent.create({ data }), get: id => db.parent.findUnique({ where: { id } }),
    update: (id, label) => db.parent.update({ where: { id }, data: { label } }), remove: id => db.parent.delete({ where: { id } }),
    orphan: () => db.child.create({ data: { id: ulid(), parentId: ulid() } }),
    json: id => db.parent.findMany({ where: { id, payload: { path: ['score'], equals: 7 } } }),
    tx: fn => db.$transaction(tx => fn(wrap(tx))), exec: text => db.$executeRawUnsafe(text), query: text => db.$queryRawUnsafe(text),
    prepared: async observed => { const before = { ...observed }; for (const value of [ulid(), ulid()]) assert.equal((await db.$queryRaw`SELECT ${value}::text AS value`)[0].value, value); const delta = protocolDelta(before, observed); assert(delta.parse >= 2 && delta.bind >= 2); return delta; },
  });
  return { ...wrap(client), close: () => client.$disconnect(), migrate: async stage => {
    const text = await runCli('prisma', ['migrate', 'diff', ...(stage ? ['--from-schema', `s${stage - 1}.prisma`] : ['--from-empty']), '--to-schema', `s${stage}.prisma`, '--script'], dir);
    const statements = text.replace(/^--.*$/gm, '').split(';').map(s => s.trim()).filter(Boolean);
    assert(statements.length > 0);
    for (const statement of statements) await client.$executeRawUnsafe(statement);
    return { statements: statements.length };
  } };
}

async function drizzleApi(dir, names, pool) {
  const schema = stage => {
    const parent = pgTable(names.parent, { id: text().primaryKey(), label: text().notNull(), payload: jsonb().notNull() }, t => stage >= 2 ? [index(`${names.parent}_label_idx`).on(t.label)] : []);
    const child = pgTable(names.child, { id: text().primaryKey(), parentId: stage >= 1 ? text().notNull().references(() => parent.id) : text().notNull() });
    return { parent, child };
  };
  const tables = schema(2), snapshots = [generateDrizzleJson({}), ...[0, 1, 2].map(stage => generateDrizzleJson(schema(stage)))];
  const wrap = db => ({
    create: async data => (await db.insert(tables.parent).values(data).returning())[0], get: async id => (await db.select().from(tables.parent).where(eq(tables.parent.id, id)))[0] ?? null,
    update: async (id, label) => (await db.update(tables.parent).set({ label }).where(eq(tables.parent.id, id)).returning())[0], remove: id => db.delete(tables.parent).where(eq(tables.parent.id, id)).returning(),
    orphan: () => db.insert(tables.child).values({ id: ulid(), parentId: ulid() }).returning(),
    json: id => db.select().from(tables.parent).where(sql`${tables.parent.id} = ${id} AND ${tables.parent.payload}->>'score' = '7'`),
    tx: fn => db.transaction(tx => fn(wrap(tx))), exec: text => db.execute(sql.raw(text)), query: async text => (await db.execute(sql.raw(text))).rows,
    prepared: async observed => { const ids = [ulid(), ulid()], query = db.select().from(tables.parent).where(eq(tables.parent.id, sql.placeholder('id'))).prepare(`matrix_${ulid()}`); for (const id of ids) await wrap(db).create({ id, label: 'prepared', payload: {} }); const before = { ...observed }; for (const id of ids) assert.equal((await query.execute({ id }))[0].id, id); const delta = protocolDelta(before, observed); assert.deepEqual(delta, { parse: 1, bind: 2 }); for (const id of ids) await wrap(db).remove(id); return delta; },
  });
  const db = drizzle(pool);
  await writeFile(join(dir, 'drizzle.config.cjs'), `module.exports = { dialect: 'postgresql', dbCredentials: { url: process.env.DATABASE_URL }, tablesFilter: ${JSON.stringify(Object.values(names))}, out: './introspected' };`);
  return { ...wrap(db), close: async () => {}, migrate: async stage => {
    const statements = await generateMigration(snapshots[stage], snapshots[stage + 1]);
    assert(statements.length > 0);
    for (const statement of statements) await db.execute(sql.raw(statement));
    return { statements: statements.length };
  } };
}

for (let repeat = 1; repeat <= repeats; repeat++) for (const tool of ['prisma', 'drizzle']) {
  const dir = await mkdtemp(join(root, '.matrix-')), suffix = ulid().toLowerCase();
  const names = { parent: `mx_${tool}_${suffix}`, child: `mx_${tool}_${suffix}_child` }, pool = new pg.Pool(config), passed = new Set();
  const observed = { parse: 0, bind: 0 }, wireErrors = [];
  pool.on('connect', client => { client.connection.on('parseComplete', () => observed.parse++); client.connection.on('bindComplete', () => observed.bind++); client.connection.on('errorMessage', error => { if (/^[A-Z0-9]{5}$/.test(error.code)) wireErrors.push(error.code); }); });
  pool.on('error', () => {});
  let api;
  const row = (check, status, detail) => { console.log(JSON.stringify({ surface, tool, repeat, check, status, detail })); if (status === 'fail') process.exitCode = 1; };
  const stage = async (check, deps, operation) => {
    const missing = deps.filter(dep => !passed.has(dep));
    if (missing.length) return row(check, 'blocked', { prerequisites: missing });
    try { const detail = await operation(); passed.add(check); row(check, 'pass', detail ?? {}); }
    catch (error) { row(check, 'fail', { codes: codes(error), context: error.matrix, assertion: error.code === 'ERR_ASSERTION' ? 'result_mismatch' : undefined }); }
  };
  try {
    await stage('connect', [], async () => { api = await (tool === 'prisma' ? prismaApi : drizzleApi)(dir, names, pool); assert.equal(Number((await api.query('SELECT 1 AS value'))[0].value), 1); });
    for (const [i, check] of checks.slice(1, 4).entries()) await stage(check, api ? (i ? ['migrate_table'] : []) : ['connect'], () => api.migrate(i));
    await stage('introspect', [], async () => {
      let text;
      if (tool === 'prisma') text = await runCli('prisma', ['db', 'pull', '--schema', 's2.prisma', '--print'], dir);
      else { await runCli('drizzle', ['introspect', '--config', 'drizzle.config.cjs'], dir); text = await readFile(join(dir, 'introspected/schema.ts'), 'utf8'); }
      for (const name of Object.values(names)) assert(text.includes(name));
      return { command: tool === 'prisma' ? 'prisma db pull --print' : 'drizzle-kit introspect' };
    });
    await stage('crud', ['migrate_table'], async () => { const id = ulid(); assert.equal((await api.create({ id, label: 'create', payload: {} })).id, id); assert.equal((await api.get(id)).label, 'create'); assert.equal((await api.update(id, 'updated')).label, 'updated'); await api.remove(id); assert.equal(await api.get(id), null); });
    await stage('transactions', ['migrate_table'], async () => {
      const id = ulid(), rollback = new Error('rollback');
      try { await api.tx(async tx => { await tx.create({ id, label: 'rollback', payload: {} }); throw rollback; }); assert.fail('Rollback did not throw'); } catch (error) { if (error !== rollback) throw error; }
      assert.equal(await api.get(id), null);
      await api.tx(async tx => { await tx.create({ id, label: 'committed', payload: {} }); assert.equal((await tx.get(id)).label, 'committed'); });
      assert.equal((await api.get(id)).label, 'committed'); await api.remove(id);
    });
    await stage('json', ['migrate_table'], async () => { const id = ulid(), payload = { score: 7, nested: ['value', true, null] }; await api.create({ id, label: 'json', payload }); assert.deepEqual((await api.get(id)).payload, payload); assert.equal((await api.json(id)).length, 1); await api.remove(id); });
    await stage('prepared', api ? (tool === 'drizzle' ? ['migrate_table'] : []) : ['connect'], async () => ({ protocol: tool === 'prisma' ? 'default unnamed Parse/Bind/Execute via adapter-pg' : 'named node-postgres Parse/Bind/Execute', namedCache: tool === 'prisma' ? 'not asserted' : 'one Parse, two Binds', observed: await api.prepared(observed) }));
    await stage('errors', ['migrate_table'], async () => {
      const id = ulid(), states = []; await api.create({ id, label: 'duplicate', payload: {} });
      const probes = [['23505', () => api.exec(`INSERT INTO "${names.parent}" (id, label, payload) VALUES ('${id}', 'duplicate', '{}')`)], ['42601', () => api.query('SELEC 1')], ['23503', () => api.orphan()]];
      for (const [state, operation] of probes) { try { await expectState(operation, state, wireErrors); states.push({ state, status: 'pass' }); } catch (error) { states.push({ state, status: 'fail', codes: codes(error) }); } }
      assert.equal(Number((await api.query('SELECT 1 AS value'))[0].value), 1); await api.remove(id);
      if (states.some(s => s.status === 'fail')) throw Object.assign(new Error('SQLSTATE assertions failed'), { matrix: { sqlstates: states } });
      return { sqlstates: states };
    });
  } finally {
    await stage('cleanup', [], async () => { for (const name of [names.child, names.parent]) await pool.query(`DROP TABLE IF EXISTS "${name}" CASCADE`); });
    await api?.close().catch(() => { row('cleanup_client', 'fail', { codes: [] }); });
    await pool.end(); await rm(dir, { recursive: true, force: true });
  }
}
