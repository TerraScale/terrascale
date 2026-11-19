import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { isDeepStrictEqual } from 'node:util';
import { createTerraBaseClient, createTerraBaseClientFromEnvironment, generateId, isValidId, staticBaseUrlResolver } from '../../../sdk/js/src/index.js';

import { createTerraScaleProjectIdentityClient } from '../../../sdk/js/src/management.js';

import { createTsAuthClient, verifyAgentToken } from '../../../sdk/js/src/ts-auth.js';

if (process.env.E2E_SESSION_FAULT) {
  await (await import('./session-fault.mjs')).sessionFault();
  process.exit(0);
}

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
  async 'auth.agents'() {
    const auth = createTsAuthClient({ apiBaseUrl: 'https://auth.terrascale.tech', instanceSlug: project });
    const credential = process.env.E2E_AUTH_WRITE_KEY;
    const readCredential = process.env.E2E_AUTH_READ_KEY;
    const owner = process.env.E2E_AUTH_USER;
    check((await auth.projects.setAgentPolicy(credential, project, { allowed_scopes: ['invoices:read'] })).ok, 'set agent policy');
    const policy = await auth.projects.getAgentPolicy(process.env.E2E_AUTH_READ_KEY, project);
    check(policy.ok && policy.value.allowed_scopes.includes('invoices:read'), 'get agent policy');
    const pair = await crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, false, ['sign', 'verify']);
    const public_jwk = await crypto.subtle.exportKey('jwk', pair.publicKey);
    const created = await auth.projects.createAgent(credential, project, owner, { name: 'SDK agent', allowed_scopes: ['invoices:read'], public_jwk });
    check(created.ok, 'create agent');
    const agentId = created.value.id;
    const kid = created.value.keys[0].kid;
    try {
      const listed = await auth.projects.listAgents(readCredential, project, { owner });
      check(listed.ok && listed.value.items.some(item => item.id === agentId), 'list agent');
      const read = await auth.projects.getAgent(readCredential, project, agentId);
      check(read.ok && read.value.owner_user_id === owner, 'get agent');
      const owned = await auth.projects.listUserAgents(readCredential, project, owner);
      check(owned.ok && owned.value.items.some(item => item.id === agentId), 'list owner agents');
      const rotated = await crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, false, ['sign', 'verify']);
      const added = await auth.projects.addAgentKey(credential, project, agentId, { public_jwk: await crypto.subtle.exportKey('jwk', rotated.publicKey) });
      check(added.ok, 'add agent key');
      check((await auth.projects.revokeAgentKey(credential, project, agentId, added.value.kid)).ok, 'revoke agent key');
      const token = await auth.agents.getToken({ project, agentId, kid, key: pair.privateKey });
      check(token.ok, 'agent token');
      writeFileSync(join(process.env.E2E_SECRETS, 'user-api-key-js-agent'), token.value.access_token, { mode: 0o600 });
      const verified = await verifyAgentToken(token.value.access_token, { project });
      check(verified.ok && verified.value.agentId === agentId && verified.value.ownerId === owner && verified.value.actingFor === 'owner', 'verify agent token');
    } finally {
      check((await auth.projects.revokeAgent(credential, project, agentId)).ok, 'revoke agent');
    }
    const fresh = createTsAuthClient({ apiBaseUrl: 'https://auth.terrascale.tech', instanceSlug: project });
    const refused = await fresh.agents.getToken({ project, agentId, kid, key: pair.privateKey });
    check(!refused.ok && refused.error.code === 'invalid_grant', 'revoked agent cannot issue a token');
  },
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
  async 'auth.user_profiles'(op) {
    const auth = createTsAuthClient({ apiBaseUrl: 'https://auth.terrascale.tech', instanceSlug: 'terrascale' }).projects;
    const write = process.env.E2E_AUTH_WRITE_KEY;
    const created = await auth.createUser(write, project, { email: `profile-js-${run}@example.com`, first_name: 'SDK', public_metadata: { keep: 1, remove: true } });
    check(created.ok && created.value.first_name === 'SDK', 'created profile');
    if (!created.ok) throw new Error('create failed');
    const changed = await auth.updateUser(write, project, created.value.id, { public_metadata: { remove: null, added: 'yes' }, private_metadata: { backend: true } });
    check(changed.ok && changed.value.public_metadata.keep === 1 && changed.value.public_metadata.added === 'yes' && !('remove' in changed.value.public_metadata), 'metadata merge');
    const imported = await auth.importUsers(write, project, { users: [
      { email: `import-js-${run}@example.com`, email_verified: true, password_hash: op.password_hash },
      { email: `invalid-js-${run}@example.com`, password_hash: 'malformed' },
    ] });
    check(imported.ok && imported.value.items.length === 1 && imported.value.errors[0]?.index === 1, 'partial import');
    if (imported.ok) for (const row of imported.value.items) check((await auth.deleteUser(write, project, row.user.id)).ok, 'delete imported');
    check((await auth.deleteUser(write, project, created.value.id)).ok, 'delete created');
  },
  async 'auth.organizations'() {
    const auth = createTsAuthClient({ apiBaseUrl: 'https://auth.terrascale.tech', instanceSlug: 'terrascale' }).projects.organizations;
    const write = process.env.E2E_AUTH_WRITE_KEY;
    const read = process.env.E2E_AUTH_READ_KEY;
    const user = process.env.E2E_AUTH_USER;
    const created = await auth.createOrganization(write, project, { name: 'SDK organization', slug: table.replaceAll('_', '-'),
      admin_user_id: user, image_url: null, public_metadata: {}, private_metadata: {}, max_members: null, domain_join: 'off' });
    check(created.ok, 'organization created');
    if (!created.ok) return;
    const id = created.value.id;
    try {
      const found = await auth.getOrganization(read, project, id);
      check(found.ok && found.value.id === id, 'organization read');
      const listed = await auth.listOrganizations(read, project);
      check(listed.ok && listed.value.items.some(item => item.id === id), 'organization listed');
      const members = await auth.listOrganizationMembers(read, project, id);
      check(members.ok && members.value.items.some(item => item.user_id === user && item.role === 'admin'), 'initial admin');
      const invite = await auth.createOrganizationInvitation(write, project, id, { email: 'org-' + run + '@example.com', role: 'member' });
      check(invite.ok, 'organization invitation created');
      const invites = await auth.listOrganizationInvitations(read, project, id);
      check(invites.ok && invites.value.items.some(item => item.status === 'pending'), 'organization invitation listed');
    } finally { check((await auth.deleteOrganization(write, project, id)).ok, 'organization deleted'); }
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
      const proxied = await client.database(created.id).regions.regions();
      same(proxied.regions.map(row => row.region), ['fra'], 'proxied nonmember endpoint');
      same(cells(await client.database(created.id).query('SELECT value FROM placement_rows')), [[1]], 'data survived placement');
      await client.database(created.id).execute('INSERT INTO placement_rows VALUES (2)');
      same(cells(await placed.database(created.id).query('SELECT value FROM placement_rows ORDER BY value')), [[1], [2]], 'acknowledged proxied write retained');
      console.log('js placement: SDK keeps its original region; proxied write acknowledged 1, found 1; rows [[1],[2]]');
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
  async 'rules.manage'() {
    const rules = state.db.rules;
    const database = process.env.E2E_DATABASE_ID;
    const prior = await rules.get().catch((error) => {
      same(error.code, 'no_rules', 'missing Rules');
      return null;
    });
    const read = "row.id = auth.uid AND auth.claims.plan = 'pro'";
    const set = { tables: { [table]: {
      read, create: "auth.claims.plan = 'pro'", columns: ['id', 'label'], writable: ['label'], set: { create: { id: 'auth.uid' } },
    } } };
    const deployed = await rules.deploy(set, prior?.version ?? null);
    const got = await rules.get();
    same([got.version, got.tables[table].read], [deployed.version, read], 'Rules read');
    const user = process.env.E2E_AUTH_USER;
    const identity = createTerraScaleProjectIdentityClient({
      apiBaseUrl: 'https://auth.terrascale.tech', project, projectApiKey: process.env.E2E_AUTH_MINT_KEY,
    });
    const minted = await identity.mintEndUserToken({ user_id: user, database, claims: { plan: 'pro', roles: ['editor'] } });
    check(minted.ok, `custom token mint: ${minted.ok ? '' : `${minted.status} ${minted.error.code} ${minted.error.message}`}`);
    const token = minted.value;
    writeFileSync(join(process.env.E2E_SECRETS, 'custom-token-js'), token.access_token, { mode: 0o600 });
    same([token.token_type, token.expires_in], ['Bearer', 300], 'custom token lifetime');
    await state.db.execute('INSERT INTO ' + table + "(rowid,id,label) VALUES (700002,'other','hidden')");
    try {
      const rows = createTerraBaseClient({ project, token: token.access_token }).database(database).rows(table);
      const created = await rows.create({ label: 'custom-token' });
      check(created.ok, `custom token Rules write: ${created.ok ? '' : `${created.status} ${created.error.code}`}`);
      same([created.status, created.value.row], [201, { id: user, label: 'custom-token' }], 'custom token written row');
      const row = await rows.get(created.value.key);
      check(row.ok, `custom token Rules read: ${row.ok ? '' : `${row.status} ${row.error.code}`}`);
      same(row.value.row, created.value.row, 'custom token readable row');
      const hidden = await rows.get('700002');
      same([hidden.ok, hidden.status, hidden.ok || hidden.error.code], [false, 404, 'row_not_found'], 'custom token hidden row');
      const update = await rows.update(created.value.key, { label: 'changed' });
      same([update.ok, update.status, update.ok || update.error.code], [false, 403, 'rule_denied'], 'custom token denied update');
    } finally {
      await state.db.execute('DELETE FROM ' + table + " WHERE rowid = 700002 OR label = 'custom-token'");
    }
    const allowed = await rules.test({ operation: 'read', table, auth: { uid: 'owner', claims: { plan: 'pro' } }, row: { id: 'owner' } });
    same([allowed.allowed, allowed.columns, allowed.problems], [true, ['id', 'label'], []], 'Rules simulator owner');
    const denied = await rules.test({ operation: 'read', table, auth: { uid: 'other', claims: { plan: 'pro' } }, row: { id: 'owner' } });
    same([denied.allowed, denied.decidedBy], [false, 'read'], 'Rules simulator other user');
    const invalid = await refused(() => rules.deploy({ tables: { [table]: { read, columns: ['missing_column'] } } }, deployed.version));
    same([invalid.status, invalid.code, invalid.details?.details?.problems?.length > 0], [400, 'invalid_rules', true], 'invalid Rules');
    const stale = await refused(() => rules.deploy(set, null));
    same([stale.status, stale.code], [412, 'version_mismatch'], 'stale Rules');
    const updated = await rules.deploy({ tables: {} }, deployed.version);
    const page = await rules.versions({ limit: 1 });
    same([page.items.length, page.items[0].version, page.next !== null], [1, updated.version, true], 'Rules first page');
    const next = await rules.versions({ limit: 1, after: page.next });
    same([next.items.length, next.items[0].version], [1, deployed.version], 'Rules second page');
    const version = await rules.version(deployed.version);
    same([version.version, version.tables[table].read], [deployed.version, read], 'Rules version');
    const restored = await rules.restore(deployed.version, updated.version);
    same([restored.version !== deployed.version, restored.tables[table].read], [true, read], 'Rules restore');
    if (prior) await rules.restore(prior.version, restored.version);
    else await rules.deploy({ tables: {} }, restored.version);
  },
  async 'functions.stats'() {
    const name = `stats-${run}`;
    await state.db.functions.deploy(name, { source: 'async function handler() { return { ok: true }; }' });
    const response = await state.db.functions.invoke(name, {});
    same(response.status, 200, 'invocation status');
    const stats = await state.db.functions.stats(name, '1h');
    same([stats.window, stats.invocations >= 1, stats.buckets.length], ['1h', true, 60], 'stats');
    const logs = await state.db.functions.logs(name, 1);
    check(logs.length === 1 && logs[0].httpStatus === 200 && logs[0].responseBytes >= 0, 'log metrics');
    await state.db.functions.delete(name);
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

const group = spec.operations.filter((op) => op.group === process.env.E2E_GROUP && (!op.sdks || op.sdks.includes("js")) && (!op.needs || process.env.E2E_REGIONS === '2' && op.id.startsWith('placement.')));
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
