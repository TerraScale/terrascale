import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { isDeepStrictEqual } from 'node:util';
import { createTerraBaseClient, createTerraBaseClientFromEnvironment, generateId, isValidId, staticBaseUrlResolver } from '../../../sdk/js/src/index.js';

import { createTsAuthClient } from '../../../sdk/js/src/ts-auth.js';

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

function origin(raw) {
  try {
    return staticBaseUrlResolver(raw)();
  } catch (error) {
    if (error instanceof TypeError) return null;
    throw error;
  }
}

async function authOrigin(raw) {
  let sent = null;
  const record = async (url) => {
    sent = new URL(url).origin;
    throw new TypeError('recorded, not sent');
  };
  let scoped;
  try {
    scoped = createTerraBaseClient({ apiKey: '01JA0000000000000000000000_0123456789abcdefghjkmnpqrstvwxyz0123456789abcdefghjk', authBaseUrl: raw, project, fetch: record });
  } catch (error) {
    if (error instanceof TypeError) return null;
    throw error;
  }
  await refused(() => scoped.createScopedKey({ database: generateId(), access: 'read' }));
  return sent;
}

async function status(work) {
  const error = await refused(work);
  return error.status;
}

const operations = {
  async 'auth.manage_user'() {
    const auth = createTsAuthClient({ apiBaseUrl: 'https://auth.terrascale.tech', instanceSlug: 'terrascale' }).projects;
    const read = process.env.E2E_AUTH_READ_KEY;
    const write = process.env.E2E_AUTH_WRITE_KEY;
    const user = process.env.E2E_AUTH_USER;
    const listed = await auth.listUsers(read, project);
    check(listed.ok && listed.value.items.some(item => item.id === user), 'read key lists the user');
    const refused = await auth.disableUser(read, project, user);
    check(!refused.ok && refused.status === 403, 'read key cannot disable');
    const disabled = await auth.disableUser(write, project, user);
    check(disabled.ok && disabled.value.disabled, 'write key disables');
    const enabled = await auth.enableUser(write, project, user);
    check(enabled.ok && !enabled.value.disabled, 'write key enables');
    const created = await auth.createUserApiKey(write, project, user, { name: 'journey', scopes: ['invoices:read'] });
    check(created.ok && created.value.key, 'create user key');
    const key = created.value;
    writeFileSync(join(process.env.E2E_SECRETS, 'user-api-key-js'), key.key, { mode: 0o600 });
    const keys = await auth.listUserApiKeys(read, project, user);
    check(keys.ok && keys.value.items.some(item => item.id === key.id), 'list user key');
    const verified = await auth.verifyUserApiKey(read, project, { key: key.key });
    check(verified.ok && verified.value.valid && verified.value.user_id === user, 'verify user key');
    same(verified.value.scopes, ['invoices:read'], 'user key scopes');
    const revoked = await auth.revokeUserApiKey(write, project, user, key.id);
    check(revoked.ok, 'revoke user key');
    const invalid = await auth.verifyUserApiKey(read, project, { key: key.key });
    check(invalid.ok && !invalid.value.valid, 'revoked key invalid immediately');

  },
  async 'placement.create'() {
    const created = await client.createDatabase(project, `e2e-js-${run}-placed`, { regions: ['gru', 'fra'] });
    try {
      same(created.regions, ['fra', 'gru'], 'created regions');
      check(Number.isInteger(created.placementVersion), 'placement version is an integer');
      const repeated = await client.setDatabasePlacement(project, created.id, { regions: ['fra', 'gru'] });
      same(repeated.placementVersion, created.placementVersion, 'unchanged placement version');
    } finally { await client.deleteDatabase(project, created.id); }
  },
  async 'placement.set'() {
    const created = await client.createDatabase(project, `e2e-js-${run}-move`, { regions: ['gru'] });
    let placed = client;
    try {
      await client.database(created.id).execute('CREATE TABLE placement_rows (value INTEGER PRIMARY KEY)');
      await client.database(created.id).execute('INSERT INTO placement_rows VALUES (1)');
      const added = await client.setDatabasePlacement(project, created.id, { regions: ['gru', 'fra'] });
      same(added.regions, ['fra', 'gru'], 'added region');
      check(added.placementVersion > created.placementVersion, 'membership raises placement version');
      const progress = await client.database(created.id).regions.regions();
      same(progress.regions.map(row => row.region), ['fra', 'gru'], 'status regions');
      same(progress.regions.find(row => row.region === 'gru').role, 'writer', 'writer role');
      const deadline = Date.now() + 90_000;
      let updated;
      for (;;) {
        try { updated = await client.setDatabasePlacement(project, created.id, { regions: ['fra'] }); break; }
        catch (error) {
          if (error.status !== 503 || error.code !== 'replica_not_ready' || Date.now() >= deadline) throw error;
          await new Promise(resolve => setTimeout(resolve, 1000));
        }
      }
      placed = createTerraBaseClientFromEnvironment({ baseUrl: 'https://fra.api.terrascale.tech', authBaseUrl: 'https://auth.terrascale.tech' });
      same(updated.regions, ['fra'], 'dropped writer');
      check(updated.placementVersion > added.placementVersion, 'writer change raises placement version');
      const after = await placed.database(created.id).regions.regions();
      same(after.regions.map(row => [row.region, row.role]), [['fra', 'writer']], 'moved status');
      const error = await refused(() => client.database(created.id).regions.regions());
      same([error.status, error.code], [421, 'wrong_region'], 'nonmember endpoint');
      same(cells(await placed.database(created.id).query('SELECT value FROM placement_rows')), [[1]], 'data survived placement');
      console.log('js placement: create [gru,fra], add fra, repeat drop gru -> 200, status fra writer, 421 wrong_region');
    } finally { await placed.deleteDatabase(project, created.id); }
  },
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
  async 'origin.rule'(op) {
    const { cases } = JSON.parse(readFileSync(join(dirname(process.env.E2E_OPERATIONS), op.cases), 'utf8'));
    const wrong = [];
    for (const [index, [raw, api, auth]] of cases.entries())
      for (const [column, expected, got] of [['api', api, origin(raw)], ['auth', auth, await authOrigin(raw)]])
        if (got !== (expected === null ? null : new URL(expected).origin))
          wrong.push(`#${index} ${JSON.stringify(raw.slice(0, 60))} ${column}: expected ${expected ?? 'refused'}, got ${got ?? 'refused'}`);
    check(wrong.length === 0, `origin rule: ${wrong.length} of ${2 * cases.length} checks differ: ${wrong.join('; ')}`);
  },
  async 'agent.create_ttl'(op) {
    const before = Date.now();
    const created = await client.createDatabase(project, fill(op.name), { ttlSeconds: op.ttlSeconds });
    const after = Date.now();
    same(created.name, fill(op.name), 'name');
    const expiry = Date.parse(created.expiresAt);
    const earliest = before + op.ttlSeconds * 1000;
    const latest = after + (op.ttlSeconds + 5) * 1000;
    check(Number.isFinite(expiry) && expiry >= earliest && expiry <= latest, `expiry the server reports, ${created.expiresAt}, is outside the TTL window`);
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
    same(await status(() => narrow.createScopedKey({ project, database: state.agent.id, access: 'read' })), op.mintStatus, 'minting with a scoped key');
  },
  async 'agent.fork'(op) {
    await state.db.execute(fill(op.setup));
    await state.db.execute(fill(op.sql));
    const fork = await client.forkDatabase(project, process.env.TERRASCALE_DATABASE_NAME, fill(op.name));
    same([fork.name, fork.branchedFrom], [fill(op.name), process.env.E2E_DATABASE_ID], 'fork');
    same(cells(await client.database(fork.id).query(fill(op.check))), op.checkRows, 'rows of the fork');
    await client.deleteDatabase(project, fork.id);
  },
  async 'agent.scoped_key_outside'(op) {
    const source = process.env.E2E_DATABASE_ID;
    const scoped = await client.createScopedKey({ project, database: source, access: op.access, ttlSeconds: op.ttlSeconds });
    writeFileSync(join(process.env.E2E_SECRETS, 'scoped-key-js-outside'), scoped.key, { mode: 0o600 });
    const narrow = createTerraBaseClientFromEnvironment({ apiKey: scoped.key });
    await narrow.database(source).query(op.sql);
    const error = await refused(() => narrow.database(process.env.E2E_OTHER_DATABASE).query(op.sql));
    same([error.status, error.code], [op.status, op.code], 'the other database');
  },
  async 'agent.delete'(op) {
    await client.deleteDatabase(project, state.agent.id);
    same(await status(() => client.database(state.agent.id).execute(op.sql)), op.deletedStatus, 'the deleted database');
  },
};

const group = spec.operations.filter((op) => op.group === process.env.E2E_GROUP && (!op.needs || process.env.E2E_REGIONS === '2' && op.id.startsWith('placement.')));
state.db = ['a', 'c'].includes(process.env.E2E_GROUP) ? client.database(process.env.TERRASCALE_DATABASE_NAME) : undefined;
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
