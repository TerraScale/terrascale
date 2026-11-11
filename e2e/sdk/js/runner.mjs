import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { isDeepStrictEqual } from 'node:util';
import { createTerraBaseClientFromEnvironment, generateId, isValidId } from '../../../sdk/js/src/index.js';

const spec = JSON.parse(readFileSync(process.env.E2E_OPERATIONS, 'utf8'));
const run = process.env.E2E_RUN_ID;
const project = process.env.TERRASCALE_PROJECT_ID;
const table = spec.table.replace('{sdk}', 'js').replace('{run}', run);
const fill = (text) => text.replaceAll('{table}', table).replaceAll('{sdk}', 'js').replaceAll('{run}', run);
const client = createTerraBaseClientFromEnvironment();
const state = {};

function check(condition, message) {
  if (!condition) throw new Error(message);
}

function same(actual, expected, what) {
  check(isDeepStrictEqual(actual, expected), `${what}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual, (_, v) => (typeof v === 'bigint' ? `${v}` : v))}`);
}

const plain = (value) => (typeof value === 'bigint' ? Number(value) : value);
const cells = (rows) => rows.map((row) => Object.values(row).map(plain));

async function refused(work) {
  try {
    await work();
  } catch (error) {
    return error;
  }
  throw new Error('the call was accepted');
}

async function status(work) {
  const error = await refused(work);
  return error.status;
}

const operations = {
  async 'sql.execute.positional'(op) {
    await state.db.execute(fill(op.setup));
    same((await state.db.execute(fill(op.sql), op.args)).rowsAffected, op.rowsAffected, 'rows affected');
  },
  async 'sql.execute.named'(op) {
    same((await state.db.execute(fill(op.sql), op.named)).rowsAffected, op.rowsAffected, 'rows affected');
  },
  async 'sql.execute.statement'(op) {
    same((await state.db.execute({ sql: fill(op.sql), args: op.args })).rowsAffected, op.rowsAffected, 'rows affected');
    same(cells(await state.db.query(fill(op.check))), op.checkRows, 'stored row');
  },
  async 'sql.query'(op) {
    same(cells(await state.db.query(fill(op.sql))), op.rows, 'rows');
  },
  async 'sql.query.typed'(op) {
    const typed = (await state.db.query(fill(op.sql), op.args)).map((row) => ({ id: String(row.id), n: Number(row.n) }));
    same(typed, op.objects, 'typed rows');
  },
  async 'sql.batch'(op) {
    const results = await state.db.batch(op.statements.map(fill));
    same(results.length, op.statements.length, 'results');
    same(results.at(-1).rows.map((row) => row.map(plain)), op.lastRows, 'last rows');
    const error = await refused(() => state.db.batch(op.failing.map(fill)));
    same([error.code, error.statement], [op.code, op.statement], 'batch refusal');
    same(cells(await state.db.query(fill(op.check))), op.checkRows, 'rolled back');
  },
  async 'sql.refused'(op) {
    const error = await refused(() => state.db.execute(fill(op.sql)));
    same([error.code, error.statement, error.sqlstate, error.status], [op.code, op.statement, op.sqlstate, op.status], 'SqlError');
  },
  async 'values.roundtrip'(op) {
    const v = op.values;
    await state.db.execute(fill(op.setup));
    const blob = Uint8Array.from(Buffer.from(v.b, 'base64'));
    await state.db.execute(fill(op.sql), [v.k, v.n, BigInt(v.big), v.r, v.flag, v.t, blob, v.absent]);
    const [row] = await state.db.query(fill(op.select));
    same([row.k, Number(row.n), String(row.big), row.r, Number(row.flag), row.t, Buffer.from(row.b).toString('base64'), row.absent],
      [v.k, v.n, v.big, v.r, op.storedFlag, v.t, v.b, v.absent], 'values');
  },
  async 'ws.connect'() {
    state.session = await state.db.connect();
  },
  async 'ws.statements'(op) {
    const session = state.session;
    same((await session.execute(fill(op.sql))).rowsAffected, 1, 'rows affected');
    same(cells(await session.query(fill(op.query))), op.rows, 'rows');
    same((await session.batch(op.statements.map(fill))).at(-1).rows.map((row) => row.map(plain)), op.lastRows, 'last rows');
  },
  async 'ws.begin_commit'(op) {
    await state.session.begin();
    await state.session.execute(fill(op.sql));
    await state.session.commit();
    same(cells(await state.db.query(fill(op.check))), op.checkRows, 'committed rows');
  },
  async 'ws.rollback'(op) {
    await state.session.begin();
    await state.session.execute(fill(op.sql));
    await state.session.rollback();
    same(cells(await state.db.query(fill(op.check))), op.checkRows, 'rolled back rows');
  },
  async 'ws.transaction.commit'(op) {
    await state.session.transaction(async (session) => {
      await session.execute(fill(op.sql));
    });
    same(cells(await state.db.query(fill(op.check))), op.checkRows, 'committed rows');
  },
  async 'ws.transaction.rethrow'(op) {
    const thrown = new Error(op.message);
    const error = await refused(() => state.session.transaction(async (session) => {
      await session.execute(fill(op.sql));
      throw thrown;
    }));
    check(error === thrown, `the transaction rethrew ${error}`);
    same(cells(await state.db.query(fill(op.check))), op.checkRows, 'rolled back rows');
  },
  async 'ws.in_transaction'() {
    await state.session.begin();
    same(state.session.inTransaction, true, 'inside');
    await state.session.commit();
    same(state.session.inTransaction, false, 'after commit');
  },
  async 'ws.close'(op) {
    await state.session.close();
    await refused(() => state.session.execute(op.sql));
  },
  async 'conflicts.query'(op) {
    const now = Date.now();
    const rows = await state.db.query(op.sql, [table, new Date(now - 3_600_000).toISOString(), new Date(now + 3_600_000).toISOString()]);
    same(cells(rows), op.rows, 'conflict rows');
  },
  async 'ids.new_validate'(op) {
    const id = generateId();
    same([isValidId(id), isValidId(op.invalid)], [true, false], 'validation');
  },
  async 'agent.create_ttl'(op) {
    const created = await client.createDatabase(project, fill(op.name), { ttlSeconds: op.ttlSeconds });
    same(created.name, fill(op.name), 'name');
    check(created.expiresAt, 'the database has an expiry');
    state.agent = created;
  },
  async 'agent.set_ttl'(op) {
    const updated = await client.setDatabaseTtl(project, state.agent.id, op.ttlSeconds);
    check(new Date(updated.expiresAt) > new Date(state.agent.expiresAt), 'the expiry moved later');
  },
  async 'agent.clear_ttl'() {
    same((await client.clearDatabaseTtl(project, state.agent.id)).expiresAt ?? null, null, 'expiry');
  },
  async 'agent.scoped_key'(op) {
    const scoped = await client.createScopedKey({ project, database: state.agent.id, access: op.access, ttlSeconds: op.ttlSeconds });
    writeFileSync(join(process.env.E2E_SECRETS, 'scoped-key-js'), scoped.key, { mode: 0o600 });
    const narrow = createTerraBaseClientFromEnvironment({ apiKey: scoped.key });
    await narrow.database(state.agent.id).execute(op.sql);
    same(await status(() => narrow.database(fill(op.outside)).execute(op.sql)), op.outsideStatus, 'outside its database');
    same(await status(() => narrow.createScopedKey({ project, database: state.agent.id, access: 'read' })), op.mintStatus, 'minting with a scoped key');
  },
  async 'agent.delete'(op) {
    await client.deleteDatabase(project, state.agent.id);
    same(await status(() => client.database(state.agent.id).execute(op.sql)), op.deletedStatus, 'the deleted database');
  },
};

const group = spec.operations.filter((op) => op.group === process.env.E2E_GROUP && !op.needs);
state.db = process.env.E2E_GROUP === 'a' ? client.database(process.env.TERRASCALE_DATABASE_NAME) : undefined;
let failed = false;
for (const op of group) {
  const started = performance.now();
  try {
    await operations[op.id](op);
    console.log(JSON.stringify({ id: op.id, ok: true, ms: Math.round(performance.now() - started) }));
  } catch (error) {
    failed = true;
    console.log(JSON.stringify({ id: op.id, ok: false, ms: Math.round(performance.now() - started), error: `${error?.name}: ${error?.message} status=${error?.status ?? ''} code=${error?.code ?? ''}` }));
  }
}
process.exitCode = failed ? 1 : 0;
