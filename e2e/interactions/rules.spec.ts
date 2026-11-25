import { randomBytes } from 'node:crypto';
import type { APIRequestContext, APIResponse, Page } from '@playwright/test';
import { expect, test as base } from '../support/audit';
import { mcpTool } from '../support/mcp';
import { signInToken } from '../support/auth-management';
import { auth } from '../support/stack';
import { waitForCreatedDatabase } from '../support/database';
import { createDedicatedProjectOwner } from '../support/project-owner';
import { saveSecret } from '../support/state';

const api = 'https://api.terrascale.tech';
const crockford = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';

type ShapeRow = Record<string, unknown>;
type ShapeBody = { handle: string; next_offset: string; changes: { ops: { op: string; row: ShapeRow }[] }[] };

type RulesOwner = Awaited<ReturnType<typeof createDedicatedProjectOwner>>;
type RulesAccount = Omit<RulesOwner, 'current'> & {
  current: RulesOwner['current'] & { databaseId: string; databaseName: string };
};

const test = base.extend<{ rulesAccount: RulesAccount }>({
  rulesAccount: async ({ browser }, use, info) => {
    const account = await createDedicatedProjectOwner(browser, info, 'Rules owner');
    try {
      const { page } = account;
      const name = 'rules-' + randomBytes(6).toString('hex');
      await page.goto('/databases');
      await page.getByRole('button', { name: 'New database', exact: true }).first().click();
      await page.locator('#database-name').fill(name);
      const databasesUrl = page.url();
      await page.locator('#create-database form').getByRole('button', { name: 'Create database', exact: true }).click();
      const created = await waitForCreatedDatabase(page, name, databasesUrl);
      try {
        await use({ ...account, current: { ...account.current, databaseId: created.id, databaseName: name } });
      } finally {
        await page.goto(`/databases/${created.id}/danger`);
        await page.locator('main').getByRole('button', { name: 'Delete database', exact: true }).click();
        const dialog = page.locator('dialog[open]');
        await dialog.locator('input[name="confirmName"]').fill(name);
        await dialog.getByRole('button', { name: 'Delete database', exact: true }).click();
        await page.waitForURL(url => url.pathname === '/databases');
        await expect(page.locator('main tr').filter({ hasText: name })).toHaveCount(0);
        if (info.status === info.expectedStatus) await account.audit.finish();
      }
    } finally {
      await account.remove();
    }
  },
});

test.describe.configure({ mode: 'serial' });

function ulid(): string {
  return '01' + Array.from(randomBytes(24), byte => crockford[byte % 32]).join('');
}

async function mintKey(request: APIRequestContext, current: RulesAccount['current']): Promise<string> {
  const token = await signInToken(request, 'rules-reads-owner');
  const response = await request.post(`${auth}/v1/terrascale/organizations/${current.organizationId}/projects/${current.projectId}/api-keys`, {
    headers: { authorization: 'Bearer ' + token }, data: { name: 'rules reads mint', scopes: ['auth:tokens:mint'] },
  });
  expect(response.status()).toBe(201);
  const key = (await response.json()).key as string;
  saveSecret('rules-reads-mint-key', key);
  return key;
}

async function endUserToken(request: APIRequestContext, key: string, user: string, current: RulesAccount['current']): Promise<string> {
  const response = await request.post(`${auth}/v1/${current.projectId}/custom-tokens`, {
    headers: { authorization: 'Bearer ' + key }, data: { user_id: user, database: current.databaseId },
  });
  expect(response.status()).toBe(200);
  const credential = (await response.json()).access_token as string;
  saveSecret('rules-reads-end-user-' + user, credential);
  return credential;
}

type Problem = { table: string; field: string; code: string; message: string };
type Simulation = { allowed: boolean; decidedBy: string; clause: string | null; results: { rule: string; value: boolean }[]; columns: string[]; problems: Problem[] };

type RulesBody = { version: string; tables: Record<string, Record<string, unknown>>; problems?: Problem[]; warnings?: Problem[]; updatedAt: string; updatedBy: string };

async function ownerToken(request: APIRequestContext, current: RulesOwner['current']): Promise<string> {
  const response = await request.post(auth + '/terrascale/oauth/token', {
    form: {
      grant_type: 'client_credentials', client_id: current.apiKey.slice(0, 26), client_secret: current.apiKey,
      project: current.projectId, scope: 'sql:read sql:write databases:read databases:write',
    },
  });
  expect(response.status()).toBe(200);
  const credential = (await response.json()).access_token as string;
  saveSecret('rules-owner-token', credential);
  return credential;
}

async function answer<T>(response: APIResponse, status: number, code?: string): Promise<T> {
  expect(response.status(), await response.text()).toBe(status);
  const body = await response.json();
  if (code) expect(body.error.code).toBe(code);
  return body as T;
}

test('rules compile, keep versions, simulate deployed and draft requests and restore an earlier version', async ({ request, rulesAccount: { current, page } }) => {
  const database = `${api}/v1/projects/${current.projectId}/databases/${current.databaseId}`;
  const table = 'rules_todos_' + randomBytes(4).toString('hex');
  const otherTable = table + '_other';
  const owner = await ownerToken(request, current);
  const headers = (extra: Record<string, string> = {}) => ({ authorization: 'Bearer ' + owner, ...extra });
  const execute = async (sql: string) => {
    const response = await request.post(database + '/sql/execute', { headers: headers(), data: { statements: [{ sql }] } });
    expect(response.status(), sql).toBe(200);
  };
  const put = (rules: unknown, precondition: Record<string, string>) =>
    request.put(database + '/rules', { headers: headers(precondition), data: rules });
  const get = (suffix = '') => request.get(database + '/rules' + suffix, { headers: headers() });

  await execute(`CREATE TABLE ${table} (id INTEGER PRIMARY KEY, owner_id TEXT NOT NULL, title TEXT NOT NULL, done INTEGER NOT NULL DEFAULT 0, secret TEXT)`);
  try {
    const before = await get();
    if (before.status() === 404) await answer(before, 404, 'no_rules');
    const existing = before.status() === 200 ? (await before.json() as RulesBody).version : null;
    const precondition: Record<string, string> = existing ? { 'if-match': `"${existing}"` } : { 'if-none-match': '*' };

    const owners = {
      read: "row.owner_id = auth.uid OR 'admin' IN auth.claims.roles",
      create: 'new.owner_id = auth.uid AND auth.is_anonymous = 0',
      update: 'row.owner_id = auth.uid AND new.owner_id = row.owner_id',
      delete: 'row.owner_id = auth.uid AND request.time > 0',
      columns: ['id', 'owner_id', 'title', 'done'],
      writable: ['title', 'done'],
      validate: ['length(new.title) BETWEEN 1 AND 200', 'new.done IN (0, 1)', "coalesce(auth.claims.plan, 'free') <> 'banned'"],
      set: { create: { owner_id: 'auth.uid', id: 'NULL' }, update: {} },
    };

    await answer(await put({ tables: { [table]: owners } }, {}), 428, 'precondition_required');

    const invalid = await answer<{ error: { details: { problems: Problem[] } } }>(await put({
      tables: {
        [table]: {
          read: 'owner_id = :sub',
          create: 'row.owner_id = auth.uid',
          update: "'x' IN ('a', auth.claims.roles)",
          delete: 'row.done_at = 1',
          validate: ['request.time > 0 AND auth.uid = request.ulid'],
          columns: ['id', 'nope'],
          writable: ['title'],
          set: { create: { title: 'auth.email' }, update: { title: 'request.ulid' } },
        },
        missing_table_for_rules: { read: 'TRUE' },
      },
    }, precondition), 400, 'invalid_rules');
    const found = invalid.error.details.problems.map(problem => `${problem.table}:${problem.field}:${problem.code}`).sort();
    expect(found).toEqual([
      `${table}:columns:unknown_column`,
      `${table}:create:reference_not_allowed`,
      `${table}:delete:unknown_column`,
      `${table}:read:unsafe_where`,
      `${table}:set.create.title:column_conflict`,
      `${table}:set.create.title:invalid_value`,
      `${table}:set.update.title:column_conflict`,
      `${table}:set.update.title:reference_not_allowed`,
      `${table}:update:unsafe_where`,
      `${table}:validate[0]:reference_not_allowed`,
      'missing_table_for_rules::unknown_table',
    ].sort());

    const leaking = { ...owners, writable: ['title', 'done', 'owner_id'], set: { create: { id: 'NULL' } } };
    const first = await answer<RulesBody>(await put({ tables: { [table]: leaking } }, precondition), 200);
    expect(first.version).toMatch(/^[0-9A-HJKMNP-TV-Z]{26}$/);
    expect(first.warnings?.map(warning => `${warning.table}:${warning.code}`)).toEqual([`${table}:unchecked_owner`]);

    const read = await get();
    const shown = await answer<RulesBody>(read, 200);
    expect(read.headers()['etag']).toBe(`"${first.version}"`);
    expect(shown).toMatchObject({ version: first.version, problems: [], updatedBy: expect.any(String) });
    expect(shown.tables[table]).toEqual(leaking);

    const stale = await put({ tables: { [table]: owners } }, { 'if-match': `"${existing ?? '01ARZ3NDEKTSV4RRFFQ69G5FAV'}"` });
    await answer(stale, 412, 'version_mismatch');
    expect(stale.headers()['etag']).toBe(`"${first.version}"`);
    await answer(await put({ tables: { [table]: owners } }, { 'if-none-match': '*' }), 412, 'version_mismatch');

    const second = await answer<RulesBody>(await put({ tables: { [table]: owners } }, { 'if-match': `"${first.version}"` }), 200);
    expect(second.warnings).toEqual([]);

    const simulate = (data: unknown, credential = owner) => request.post(database + '/rules:test', {
      headers: { authorization: 'Bearer ' + credential }, data,
    });
    const subject = '01ARZ3NDEKTSV4RRFFQ69G5FAE';
    const simulated = {
      operation: 'update', table, auth: { uid: subject, claims: { roles: ['editor'] }, org_id: null, org_role: null, is_anonymous: false },
      row: { owner_id: subject, title: 'a', done: 0 }, new: { title: "b'); DROP TABLE todos; --" }, time: 1763337600000,
    };
    const allowed = await answer<Simulation>(await simulate(simulated), 200);
    expect(allowed).toEqual({
      allowed: true, decidedBy: 'update', clause: owners.update,
      results: [{ rule: 'read', value: true }, { rule: 'update', value: true }, ...owners.validate.map((_, index) => ({ rule: `validate[${index}]`, value: true }))],
      columns: owners.columns, problems: [],
    });
    console.log('POST /rules:test allowed -> 200 ' + JSON.stringify(allowed));
    const denied = await answer<Simulation>(await simulate({ ...simulated, auth: { ...simulated.auth, uid: 'someone-else', claims: { roles: ['admin'] } } }), 200);
    expect(denied).toEqual({
      allowed: false, decidedBy: 'update', clause: owners.update, results: [{ rule: 'read', value: true }, { rule: 'update', value: false }], columns: owners.columns, problems: [],
    });
    console.log('POST /rules:test denied -> 200 ' + JSON.stringify(denied));
    console.log('rules simulator request: ' + JSON.stringify(simulated));

    for (const operation of ['update', 'delete']) {
      const unreadable = await answer<Simulation>(await simulate({
        ...simulated, operation, rules: { tables: { [table]: { ...owners, read: 'FALSE', update: 'TRUE', delete: 'TRUE' } } },
      }), 200);
      expect(unreadable).toEqual({
        allowed: false, decidedBy: 'read', clause: 'FALSE', results: [{ rule: 'read', value: false }], columns: owners.columns, problems: [],
      });
      console.log('POST /rules:test unreadable ' + operation + ' -> 200 ' + JSON.stringify(unreadable));
    }

    const validation = await answer<Simulation>(await simulate({ ...simulated, new: { title: '' } }), 200);
    expect(validation).toMatchObject({ allowed: false, decidedBy: 'validate[0]', clause: owners.validate[0] });
    const stamped = {
      tables: { [table]: { ...owners, update: owners.update + ' AND new.secret = request.time', set: { ...owners.set, update: { secret: 'request.time' } } } },
    };
    expect((await answer<Simulation>(await simulate({ ...simulated, rules: stamped }), 200)).allowed).toBe(true);
    expect((await answer<Simulation>(await simulate({ ...simulated, operation: 'create', new: { title: 'created', done: 0 } }), 200)).allowed).toBe(true);
    const readable = await answer<Simulation>(await simulate({ ...simulated, operation: 'read', auth: { uid: 'another-user', claims: { roles: ['admin'] } } }), 200);
    expect(readable).toMatchObject({ allowed: true, columns: owners.columns });
    expect((await answer<Simulation>(await simulate({ ...simulated, operation: 'read', auth: { uid: 'another-user', claims: { roles: 'admin' } } }), 200)).allowed).toBe(false);
    const unwritable = await answer<Simulation>(await simulate({ ...simulated, new: { owner_id: subject } }), 200);
    expect(unwritable).toMatchObject({ allowed: false, decidedBy: 'writable', clause: 'owner_id' });

    const problem = await answer<Simulation>(await simulate({ ...simulated, rules: { tables: { [table]: { update: 'new.missing = 1' } } } }), 200);
    expect(problem).toMatchObject({ allowed: false, results: [], problems: [{ table, field: 'update', code: 'unknown_column', message: expect.any(String) }] });
    console.log('POST /rules:test draft problem -> 200 ' + JSON.stringify(problem));

    const signedIn = await signInToken(page.request, 'rules-simulator-owner');
    const createdKey = await request.post(`${auth}/v1/terrascale/organizations/${current.organizationId}/projects/${current.projectId}/api-keys`, {
      headers: { authorization: 'Bearer ' + signedIn }, data: { name: 'Rules simulator mint', scopes: ['auth:tokens:mint'] },
    });
    const mintKey = (await answer<{ key: string }>(createdKey, 201)).key;
    saveSecret('rules-simulator-mint-key', mintKey);
    const minted = await request.post(`${auth}/v1/${current.projectId}/custom-tokens`, {
      headers: { authorization: 'Bearer ' + mintKey }, data: { user_id: subject },
    });
    const endUser = (await answer<{ access_token: string }>(minted, 200)).access_token;
    saveSecret('rules-simulator-end-user', endUser);
    expect((await simulate(simulated, endUser)).status()).toBe(403);
    console.log('POST /rules:test end-user token -> 403');
    const count = await request.post(database + '/sql/execute', { headers: headers(), data: { statements: [{ sql: `SELECT count(*) AS total FROM ${table}` }] } });
    expect((await answer<{ results: { rows: number[][] }[] }>(count, 200)).results[0].rows).toEqual([[0]]);

    const versions = await answer<{ items: { version: string; digest: string }[]; next: string | null }>(await get('/versions?limit=1'), 200);
    expect(versions.items.map(item => item.version)).toEqual([second.version]);
    expect(versions.next).toBe(second.version);
    const older = await answer<{ items: { version: string; digest: string }[] }>(await get(`/versions?limit=1&after=${second.version}`), 200);
    expect(older.items.map(item => item.version)).toEqual([first.version]);
    expect(older.items[0].digest).toMatch(/^[0-9a-f]{64}$/);
    expect((await answer<RulesBody>(await get('/versions/' + first.version), 200)).tables[table]).toEqual(leaking);
    await answer(await get('/versions/01ARZ3NDEKTSV4RRFFQ69G5FAV'), 404, 'unknown_version');

    const restore = (version: string, extra: Record<string, string>) =>
      request.post(`${database}/rules/versions/${version}/restore`, { headers: headers(extra) });
    await answer(await restore(first.version, {}), 428, 'precondition_required');
    const restored = await answer<RulesBody>(await restore(first.version, { 'if-match': `"${second.version}"` }), 200);
    expect(restored.version).not.toBe(first.version);
    expect(restored.tables[table]).toEqual(leaking);
    expect((await answer<{ items: { version: string }[] }>(await get('/versions?limit=3'), 200)).items.map(item => item.version))
      .toEqual([restored.version, second.version, first.version]);

    await execute(`CREATE TABLE ${otherTable} (id INTEGER PRIMARY KEY, flag INTEGER)`);
    const withOther = { tables: { ...restored.tables, [otherTable]: { read: 'row.flag = 1' } } };
    const otherVersion = await answer<RulesBody>(await put(withOther, { 'if-match': `"${restored.version}"` }), 200);
    await execute(`DROP TABLE ${otherTable}`);
    const deployedRead = await answer<Simulation>(await simulate({ ...simulated, operation: 'read' }), 200);
    expect(deployedRead).toMatchObject({ allowed: true, problems: [], results: [{ rule: 'read', value: true }] });
    const draftRead = await answer<Simulation>(await simulate({ ...simulated, operation: 'read', rules: withOther }), 200);
    expect(draftRead).toMatchObject({ allowed: false, results: [], problems: [{ table: otherTable, code: 'unknown_table' }] });
    console.log('POST /rules:test deployed after unrelated drift -> 200 ' + JSON.stringify(deployedRead));
    console.log('POST /rules:test draft after unrelated drift -> 200 ' + JSON.stringify(draftRead));
    const clean = await answer<RulesBody>(await put({ tables: restored.tables }, { 'if-match': `"${otherVersion.version}"` }), 200);

    await execute(`DROP TABLE ${table}`);
    const drifted = await answer<RulesBody>(await get(), 200);
    expect(drifted.problems?.map(problem => `${problem.table}:${problem.code}`)).toEqual([`${table}:unknown_table`]);
    await answer(await restore(second.version, { 'if-match': `"${clean.version}"` }), 400, 'invalid_rules');
    await answer(await put({ tables: {} }, { 'if-match': `"${clean.version}"` }), 200);
  } finally {
    await execute(`DROP TABLE IF EXISTS ${table}`);
    await execute(`DROP TABLE IF EXISTS ${otherTable}`);
  }
});

test('end users read only the rows and columns the rules allow, under handles bound to the user and the rules version', async ({ request, rulesAccount: { current, page } }) => {
  const database = `${api}/v1/projects/${current.projectId}/databases/${current.databaseId}`;
  const table = 'rules_notes_' + randomBytes(4).toString('hex');
  const shape = `${database}/tables/${table}/shape`;
  const rows = `${database}/tables/${table}/rows/`;
  const owner = await ownerToken(request, current);
  const headers = (extra: Record<string, string> = {}) => ({ authorization: 'Bearer ' + owner, ...extra });
  const execute = async (sql: string) => {
    const response = await request.post(database + '/sql/execute', { headers: headers(), data: { statements: [{ sql }] } });
    expect(response.status(), sql).toBe(200);
  };
  const deploy = async (tables: Record<string, unknown>) => {
    const deployed = await answer<RulesBody>(await request.get(database + '/rules', { headers: headers() }), 200);
    return answer<RulesBody>(await request.put(database + '/rules', { headers: headers({ 'if-match': `"${deployed.version}"` }), data: { tables } }), 200);
  };
  const [alice, bob] = [ulid(), ulid()];
  await answer(await request.put(database + '/rules', {
    headers: headers({ 'if-none-match': '*' }), data: { tables: {} },
  }), 200);

  await execute(`CREATE TABLE ${table} (id INTEGER PRIMARY KEY, owner TEXT NOT NULL, body TEXT NOT NULL, secret TEXT NOT NULL)`);
  try {
    await execute(`INSERT INTO ${table} (id, owner, body, secret) VALUES (1, '${alice}', 'a1', 'pin-1111'), (2, '${alice}', 'a2', 'pin-2222'), (3, '${bob}', 'b1', 'pin-3333')`);
    const mint = await mintKey(page.request, current);
    const aliceToken = await endUserToken(request, mint, alice, current);
    const bobToken = await endUserToken(request, mint, bob, current);
    const read = (credential: string, query: string, maxRedirects = 20) =>
      request.get(`${shape}?${query}`, { headers: { authorization: 'Bearer ' + credential }, maxRedirects });
    const row = (credential: string, key: string) => request.get(rows + key, { headers: { authorization: 'Bearer ' + credential } });

    const denied = await answer<{ error: { details: Record<string, string> } }>(await read(aliceToken, 'offset=-1'), 403, 'rule_denied');
    expect(denied.error.details).toEqual({ operation: 'read', table });

    await deploy({ [table]: { read: 'row.owner = auth.uid', columns: ['id', 'owner', 'body'] } });
    for (const query of ["where=secret = 'pin-1111'", 'where=length(secret) > 0', 'columns=id,secret']) {
      const hidden = await answer<{ error: { details: Record<string, string> } }>(await read(aliceToken, 'offset=-1&' + query), 403, 'column_not_readable');
      expect(hidden.error.details).toEqual({ column: 'secret' });
    }

    const bootstrap = async (credential: string) => {
      const redirect = await read(credential, 'offset=-1', 0);
      expect(redirect.status(), await redirect.text()).toBe(307);
      const handle = redirect.headers()['tb-shape-handle'];
      const body = await answer<ShapeBody>(await request.get(api + redirect.headers()['location'], { headers: { authorization: 'Bearer ' + credential } }), 200);
      expect(body.handle).toBe(handle);
      return { handle, body, rows: body.changes.flatMap(change => change.ops.map(op => op.row)) };
    };
    const aliceShape = await bootstrap(aliceToken);
    const bobShape = await bootstrap(bobToken);
    expect(aliceShape.handle).not.toBe(bobShape.handle);
    expect(aliceShape.rows).toEqual([{ id: 1, owner: alice, body: 'a1' }, { id: 2, owner: alice, body: 'a2' }]);
    expect(bobShape.rows).toEqual([{ id: 3, owner: bob, body: 'b1' }]);

    expect(await answer(await row(aliceToken, '1'), 200)).toEqual({ row: { id: 1, owner: alice, body: 'a1' } });
    await answer(await row(aliceToken, '3'), 404, 'row_not_found');
    await answer(await row(aliceToken, '99'), 404, 'row_not_found');
    await answer(await request.get(rows + '1?columns=secret', { headers: { authorization: 'Bearer ' + aliceToken } }), 403, 'column_not_readable');
    expect(await answer(await row(owner, '3'), 200)).toEqual({ row: { id: 3, owner: bob, body: 'b1', secret: 'pin-3333' } });

    await deploy({ [table]: { read: 'row.owner = auth.uid', columns: ['id', 'body'] } });
    const stale = await read(aliceToken, `handle=${aliceShape.handle}&offset=${aliceShape.body.next_offset}`);
    const refetch = await answer<{ error: { handle: string } }>(stale, 409, 'must_refetch');
    expect(refetch.error.handle).not.toBe(aliceShape.handle);
    expect(stale.headers()['tb-shape-handle']).toBe(refetch.error.handle);
    expect((await bootstrap(aliceToken)).rows).toEqual([{ id: 1, body: 'a1' }, { id: 2, body: 'a2' }]);
    expect(await answer(await row(aliceToken, '2'), 200)).toEqual({ row: { id: 2, body: 'a2' } });
    await answer(await read(aliceToken, 'offset=-1&columns=id,owner'), 403, 'column_not_readable');
    await answer(await request.get(rows + '1?columns=owner', { headers: { authorization: 'Bearer ' + aliceToken } }), 403, 'column_not_readable');

    await execute(`ALTER TABLE ${table} DROP COLUMN body`);
    await answer(await read(aliceToken, 'offset=-1'), 403, 'rules_invalid');
    await answer(await row(aliceToken, '1'), 403, 'rules_invalid');
    await deploy({});
  } finally {
    await execute(`DROP TABLE IF EXISTS ${table}`);
  }
});

test('one subject with different trusted claims never shares a shape handle, and where refuses functions outside the allowlist', async ({ request, rulesAccount: { current, page } }) => {
  const database = `${api}/v1/projects/${current.projectId}/databases/${current.databaseId}`;
  const table = 'rules_teams_' + randomBytes(4).toString('hex');
  const shape = `${database}/tables/${table}/shape`;
  const owner = await ownerToken(request, current);
  const headers = (extra: Record<string, string> = {}) => ({ authorization: 'Bearer ' + owner, ...extra });
  const execute = async (sql: string) => {
    const response = await request.post(database + '/sql/execute', { headers: headers(), data: { statements: [{ sql }] } });
    expect(response.status(), sql).toBe(200);
  };
  const read = (credential: string, query: string, maxRedirects = 20) =>
    request.get(`${shape}?${query}`, { headers: { authorization: 'Bearer ' + credential }, maxRedirects });
  const bootstrap = async (credential: string) => {
    const redirect = await read(credential, 'offset=-1', 0);
    expect(redirect.status(), await redirect.text()).toBe(307);
    const body = await answer<ShapeBody>(await request.get(api + redirect.headers()['location'], { headers: { authorization: 'Bearer ' + credential } }), 200);
    expect(body.handle).toBe(redirect.headers()['tb-shape-handle']);
    return { handle: body.handle, body, rows: body.changes.flatMap(change => change.ops.map(op => op.row)) };
  };

  await execute(`CREATE TABLE ${table} (id INTEGER PRIMARY KEY, team TEXT NOT NULL, body TEXT NOT NULL)`);
  try {
    await execute(`INSERT INTO ${table} (id, team, body) VALUES (1, 'red', 'r1'), (2, 'blue', 'b1')`);
    for (const where of ['length(zeroblob(100000000)) > 0', '"zeroblob"(10) IS NOT NULL', "printf('%s', body) <> ''", "hex(randomblob(16)) <> ''", "unicode(body) > 0", `(1, 'red', 'r1') IN ${table}`, 'id IN (SELECT 1)', 'id = #a']) {
      await answer(await read(owner, 'offset=-1&where=' + encodeURIComponent(where), 0), 400, 'unsafe_where');
    }
    for (const where of ["lower(body) = 'r1' AND coalesce(team, '') IN ('red', 'blue')", "CAST(id AS VARCHAR(10)) = '1'"]) {
      expect((await read(owner, 'offset=-1&where=' + encodeURIComponent(where), 0)).status(), where).toBe(307);
    }

    await answer(await request.put(database + '/rules', {
      headers: headers({ 'if-none-match': '*' }), data: { tables: { [table]: { read: 'row.team = auth.claims.team' } } },
    }), 200);
    const mint = await mintKey(page.request, current);
    const user = ulid();
    const teamToken = async (team: string) => {
      const response = await request.post(`${auth}/v1/${current.projectId}/custom-tokens`, {
        headers: { authorization: 'Bearer ' + mint }, data: { user_id: user, database: current.databaseId, claims: { team } },
      });
      expect(response.status()).toBe(200);
      const credential = (await response.json()).access_token as string;
      saveSecret('rules-claims-end-user-' + team, credential);
      return credential;
    };
    const redToken = await teamToken('red');
    const blueToken = await teamToken('blue');
    const red = await bootstrap(redToken);
    const blue = await bootstrap(blueToken);
    expect(red.handle, 'callers with one subject but different trusted claims hold different shape handles').not.toBe(blue.handle);
    expect(red.rows).toEqual([{ id: 1, team: 'red', body: 'r1' }]);
    expect(blue.rows).toEqual([{ id: 2, team: 'blue', body: 'b1' }]);

    const crossed = await answer<{ error: { handle: string } }>(await read(blueToken, `handle=${red.handle}&offset=${red.body.next_offset}`), 409, 'must_refetch');
    expect(crossed.error.handle, "a caller presenting another claim set's handle is sent to its own shape").toBe(blue.handle);
    const page0 = await read(blueToken, `handle=${red.handle}&offset=snap.${red.body.next_offset}.0`);
    expect(page0.status(), "a caller cannot page another claim set's snapshot").toBe(409);

    const again = await bootstrap(await teamToken('red'));
    expect(again.handle, 'a fresh token with the same claims reuses the shape').toBe(red.handle);
    expect(again.rows).toEqual(red.rows);
    await answer(await request.put(database + '/rules', {
      headers: headers({ 'if-match': `"${(await answer<RulesBody>(await request.get(database + '/rules', { headers: headers() }), 200)).version}"` }), data: { tables: {} },
    }), 200);
  } finally {
    await execute(`DROP TABLE IF EXISTS ${table}`);
  }
});

test('end-user SQL runs one ruled statement and every bypass of the rules is refused', async ({ request, rulesAccount: { current, page } }) => {
  const database = `${api}/v1/projects/${current.projectId}/databases/${current.databaseId}`;
  const suffix = randomBytes(4).toString('hex');
  const notes = 'rules_sql_notes_' + suffix;
  const ledger = 'rules_sql_ledger_' + suffix;
  const vault = 'rules_sql_vault_' + suffix;
  const renamed = 'rules_sql_renamed_' + suffix;
  const view = 'rules_sql_view_' + suffix;
  const audit = 'rules_sql_audit_' + suffix;
  const tasks = 'rules_sql_tasks_' + suffix;
  const taskAudit = 'rules_sql_task_audit_' + suffix;
  const cteTrigger = 'rules_sql_cte_' + suffix;
  const owner = await ownerToken(request, current);
  const ownerHeaders = { authorization: 'Bearer ' + owner };
  const run = (credential: string, sql: string) =>
    request.post(database + '/sql/execute', { headers: { authorization: 'Bearer ' + credential }, data: { statements: [{ sql }] } });
  const execute = async (sql: string) => {
    const response = await run(owner, sql);
    expect(response.status(), sql).toBe(200);
    return (await response.json() as { results: { rows: unknown[][] }[] }).results[0].rows;
  };
  const deploy = async (tables: Record<string, unknown>) => {
    const live = await request.get(database + '/rules', { headers: ownerHeaders });
    const precondition: Record<string, string> = live.status() === 404 ? { 'if-none-match': '*' } : { 'if-match': `"${(await answer<RulesBody>(live, 200)).version}"` };
    return answer<RulesBody>(await request.put(database + '/rules', { headers: { ...ownerHeaders, ...precondition }, data: { tables } }), 200);
  };
  const [alice, bob] = [ulid(), ulid()];

  await execute(`CREATE TABLE ${notes} (id INTEGER PRIMARY KEY, owner TEXT NOT NULL, body TEXT NOT NULL, secret TEXT NOT NULL)`);
  await execute(`CREATE TABLE ${ledger} (entry INTEGER PRIMARY KEY, owner TEXT NOT NULL, amount INTEGER NOT NULL)`);
  await execute(`CREATE TABLE ${vault} (id INTEGER PRIMARY KEY, pin TEXT NOT NULL)`);
  await execute(`CREATE TABLE ${tasks} (id INTEGER PRIMARY KEY, assignee TEXT NOT NULL, title TEXT NOT NULL)`);
  try {
    await execute(`INSERT INTO ${notes} (id, owner, body, secret) VALUES (1, '${alice}', 'a1', 'pin-1111'), (2, '${alice}', 'a2', 'pin-2222'), (3, '${bob}', 'b1', 'pin-3333')`);
    await execute(`INSERT INTO ${ledger} (entry, owner, amount) VALUES (41, '${alice}', 100), (42, '${bob}', 200)`);
    await execute(`INSERT INTO ${vault} (id, pin) VALUES (1, 'vault-9999')`);
    await execute(`INSERT INTO ${tasks} (id, assignee, title) VALUES (1, '${alice}', 't1')`);
    await execute(`CREATE VIEW ${view} AS SELECT id, owner, body FROM ${notes}`);
    await deploy({
      [notes]: { read: 'row.owner = auth.uid', columns: ['id', 'owner', 'body'], update: 'row.owner = auth.uid', delete: 'row.owner = auth.uid', writable: ['body'] },
      [ledger]: { read: 'row.owner = auth.uid', columns: ['owner'] },
      [view]: { read: 'row.owner = auth.uid' },
      [tasks]: { read: 'row.assignee = auth.uid', columns: ['id', 'title'], update: 'row.assignee = auth.uid', writable: ['title'] },
    });
    const mint = await mintKey(page.request, current);
    const aliceToken = await endUserToken(request, mint, alice, current);

    const allowed = async (sql: string) => {
      const response = await run(aliceToken, sql);
      const body = await answer<{ results: { rows: unknown[][] }[] }>(response, 200);
      console.log('end-user SQL allowed -> 200 ' + JSON.stringify({ sql, rows: body.results[0].rows }));
      return body.results[0].rows;
    };
    expect(await allowed(`SELECT id, body FROM ${notes} ORDER BY id`)).toEqual([[1, 'a1'], [2, 'a2']]);
    expect(await allowed(`SELECT n.body FROM ${notes} AS n WHERE n.id = 2;`)).toEqual([['a2']]);
    expect(await allowed(`WITH mine AS (SELECT id FROM ${notes}) SELECT count(*) FROM mine`)).toEqual([[2]]);
    expect(await allowed(`SELECT owner FROM main.${ledger}`)).toEqual([[alice]]);
    expect(await allowed(`SELECT '--not a comment' AS text FROM ${notes} WHERE id = 1`)).toEqual([['--not a comment']]);
    await allowed(`UPDATE ${notes} SET body = 'a1-edited' WHERE id = 1`);
    await allowed(`UPDATE ${notes} SET body = 'stolen' WHERE id = 3`);
    expect(await execute(`SELECT id, body FROM ${notes} ORDER BY id`)).toEqual([[1, 'a1-edited'], [2, 'a2'], [3, 'b1']]);
    expect(await allowed(`SELECT DISTINCT m.body FROM ${ledger} AS l JOIN ${notes} AS n ON n.owner = l.owner, ${notes} AS m ORDER BY m.body`)).toEqual([['a1-edited'], ['a2']]);
    expect(await allowed(`SELECT DISTINCT m.body FROM (${notes} AS m JOIN ${ledger} AS l ON l.owner = m.owner) ORDER BY m.body`)).toEqual([['a1-edited'], ['a2']]);
    expect(await allowed(`SELECT count(*) FROM ${notes} AS a, ${notes} AS b`)).toEqual([[4]]);
    expect(await allowed(`SELECT body FROM ${view} ORDER BY body`)).toEqual([['a1-edited'], ['a2']]);
    expect(await allowed(`SELECT * FROM ${notes} ORDER BY id`)).toEqual([[1, alice, 'a1-edited'], [2, alice, 'a2']]);
    expect(await allowed(`SELECT rowid, body FROM ${notes} ORDER BY rowid`)).toEqual([[1, 'a1-edited'], [2, 'a2']]);
    expect(await allowed(`SELECT rowid AS k, title FROM ${tasks}`)).toEqual([[1, 't1-edited']]);
    await allowed(`UPDATE ${tasks} SET title = 't1-edited' WHERE id = 1`);
    expect(await allowed(`SELECT * FROM ${tasks}`)).toEqual([[1, 't1-edited']]);

    const refused = async (sql: string, code: string, details?: Record<string, string>) => {
      const response = await run(aliceToken, sql);
      const body = await answer<{ error: { code: string; message: string; details?: Record<string, string> } }>(response, 403, code);
      if (details) expect(body.error.details).toEqual(details);
      console.log('end-user SQL refused -> 403 ' + JSON.stringify({ sql, error: body.error }));
    };
    await refused(`SELECT v.pin FROM ${notes}, ${vault} AS v`, 'rule_denied', { operation: 'read', table: vault });
    await refused(`SELECT pin FROM ${notes} JOIN (${vault})`, 'rule_denied', { operation: 'read', table: vault });
    await refused(`WITH ${vault} AS (SELECT 1) SELECT 1; SELECT pin FROM ${vault}`, 'rule_denied');
    await refused(`WITH ${vault} AS (SELECT 1) SELECT pin FROM main.${vault}`, 'rule_denied', { operation: 'read', table: vault });
    await refused(`SELECT id FROM ${notes} WHERE id IN ${vault}`, 'rule_denied');
    await refused(`SELECT count(*) FROM '${notes}'`, 'rule_denied');
    await refused(`SELECT count(*) FROM ${notes} AS a, '${notes}'`, 'rule_denied');
    await refused(`SELECT 1 FROM ${notes} WHERE id IN '${notes}'`, 'rule_denied');
    await refused(`UPDATE ${notes} SET 'owner' = '${bob}' WHERE id = 1`, 'column_not_writable', { column: 'owner' });
    await refused(`UPDATE ${tasks} SET 'assignee' = '${bob}' WHERE id = 1`, 'column_not_writable', { column: 'assignee' });
    await refused(`SELECT 1 FROM ${notes} AS n JOIN ${ledger} AS l USING (owner), ${vault}`, 'rule_denied', { operation: 'read', table: vault });
    await refused(`SELECT 1 FROM ${notes} AS n JOIN ${ledger} AS l ON l.owner = n.owner, (${vault})`, 'rule_denied', { operation: 'read', table: vault });
    await refused(`WITH a AS (WITH b AS (SELECT id FROM ${notes}) SELECT id FROM b) SELECT id FROM a`, 'rule_denied');
    await refused(`WITH _tb_rule_0 AS (SELECT pin FROM ${vault}) SELECT pin FROM _tb_rule_0`, 'rule_denied');
    await refused(`UPDATE ${notes} SET body = body WHERE id = 1 RETURNING secret`, 'rule_denied');
    await refused(`UPDATE ${notes} SET body = body WHERE id = 1 RETURNING *`, 'rule_denied');
    await refused(`DELETE FROM ${notes} WHERE id = 1 RETURNING ${notes}.*`, 'rule_denied');
    await refused(`UPDATE ${notes} SET (body, owner) = ('x', '${bob}') WHERE id = 1`, 'column_not_writable', { column: 'owner' });

    await refused(`UPDATE ${notes} SET body = 'pwned' --`, 'rule_denied');
    await refused(`DELETE FROM ${notes} --`, 'rule_denied');
    await refused(`UPDATE ${notes} SET body = 'pwned' /* hidden */`, 'rule_denied');
    await refused(`SELECT id FROM ${notes}; SELECT 1`, 'rule_denied');
    await refused(`UPDATE OR REPLACE ${notes} SET body = 'pwned' WHERE id = 1`, 'rule_denied');
    await refused(`SELECT rowid FROM ${ledger}`, 'column_not_readable', { column: 'rowid' });
    await refused(`SELECT _rowid_, owner FROM ${ledger}`, 'column_not_readable', { column: '_rowid_' });
    await refused(`SELECT entry FROM ${ledger}`, 'column_not_readable', { column: 'entry' });
    await refused(`UPDATE ${notes} SET body = secret WHERE id = 1`, 'column_not_readable', { column: 'secret' });
    await refused('PRAGMA foreign_key_check', 'rule_denied');
    await refused(`PRAGMA table_info('${notes}')`, 'rule_denied');
    await refused(`SELECT name FROM pragma_table_info('${notes}')`, 'rule_denied');
    await refused(`ALTER TABLE ${notes} RENAME TO _tb_x`, 'rule_denied');

    await execute(`CREATE TRIGGER ${audit} AFTER UPDATE ON ${notes} BEGIN INSERT INTO ${vault} (pin) VALUES (OLD.secret); END`);
    const triggered = await run(aliceToken, `UPDATE ${notes} SET body = 'via trigger' WHERE id = 2`);
    await answer(triggered, 403, 'sql_sandbox');
    console.log('end-user UPDATE whose trigger reads a hidden column -> 403 sql_sandbox');
    await execute(`DROP TRIGGER ${audit}`);
    await execute(`CREATE TRIGGER ${taskAudit} AFTER UPDATE ON ${tasks} BEGIN INSERT INTO ${vault} (pin) VALUES (OLD.assignee); END`);
    await answer(await run(aliceToken, `UPDATE ${tasks} SET title = 'via trigger' WHERE id = 1`), 403, 'sql_sandbox');
    console.log('end-user UPDATE whose trigger reads a column only the update rule uses -> 403 sql_sandbox');
    await execute(`DROP TRIGGER ${taskAudit}`);
    await execute(`CREATE TRIGGER ${view} AFTER UPDATE ON ${tasks} BEGIN INSERT INTO ${vault} (pin) VALUES (OLD.assignee); END`);
    await answer(await run(aliceToken, `UPDATE ${tasks} SET title = 'via view trigger' WHERE id IN (SELECT id FROM ${view})`), 403, 'sql_sandbox');
    console.log('end-user UPDATE firing a trigger named like a ruled view -> 403 sql_sandbox');
    await execute(`DROP TRIGGER ${view}`);
    await execute(`CREATE TRIGGER ${cteTrigger} AFTER UPDATE ON ${tasks} BEGIN INSERT INTO ${vault} (pin) VALUES (OLD.assignee); END`);
    await answer(await run(aliceToken, `WITH ${cteTrigger} AS (SELECT 1) UPDATE ${tasks} SET title = 'via cte trigger' WHERE id = 1`), 403, 'sql_sandbox');
    console.log('end-user UPDATE declaring a CTE named like a trigger -> 403 sql_sandbox');
    await execute(`DROP TRIGGER ${cteTrigger}`);
    expect(await allowed(`SELECT body FROM ${view} ORDER BY body`)).toEqual([['a1-edited'], ['a2']]);
    expect(await execute(`SELECT pin FROM ${vault} ORDER BY id`)).toEqual([['vault-9999']]);
    expect(await execute(`SELECT assignee, title FROM ${tasks}`)).toEqual([[alice, 't1-edited']]);
    expect(await execute(`SELECT id, body, secret FROM ${notes} ORDER BY id`)).toEqual([[1, 'a1-edited', 'pin-1111'], [2, 'a2', 'pin-2222'], [3, 'b1', 'pin-3333']]);

    for (const sql of [
      `ALTER TABLE ${vault} RENAME TO _tb_${suffix}`,
      `ALTER TABLE ${vault} RENAME TO "_tb_${suffix}"`,
      `ALTER TABLE ${vault} RENAME TO [_tb_${suffix}]`,
      `ALTER TABLE ${vault} RENAME TO \`_tb_${suffix}\``,
      `ALTER TABLE ${vault} RENAME TO '_tb_${suffix}'`,
      `ALTER TABLE ${vault} RENAME /* hop */ TO -- hop\n _tb_${suffix}`,
      "PRAGMA table_info('_tb_meta')",
      "SELECT name FROM pragma_table_info('_tb_meta')",
    ]) {
      const response = await run(owner, sql);
      await answer(response, 403, 'sql_sandbox');
      console.log('owner SQL on internal names refused -> 403 ' + sql);
    }
    await execute(`ALTER TABLE ${vault} RENAME TO ${renamed}`);
    expect(await execute(`SELECT pin FROM ${renamed}`)).toEqual([['vault-9999']]);
    await deploy({});
  } finally {
    await execute(`DROP VIEW IF EXISTS ${view}`);
    for (const table of [notes, ledger, vault, renamed, tasks]) await execute(`DROP TABLE IF EXISTS ${table}`);
  }
});

async function setRules(page: Page, rules: unknown): Promise<void> {
  const source = JSON.stringify(rules, null, 2);
  await page.locator('#rules-source').evaluate((editor: HTMLTextAreaElement, text: string) => {
    editor.value = text;
    editor.dispatchEvent(new Event('input', { bubbles: true }));
  }, source);
  await expect(page.locator('#rules-source')).toHaveValue(source);
}

async function playground(page: Page, operation: string, table: string, auth: unknown, row: unknown): Promise<void> {
  await page.locator('#rules-operation').selectOption(operation);
  await page.locator('#rules-table').fill(table);
  await page.locator('#rules-auth').fill(JSON.stringify(auth));
  await page.locator('#rules-row').fill(JSON.stringify(row));
  await page.locator('#rules-new').fill('{}');
  await page.getByRole('button', { name: 'Run test', exact: true }).click();
}

test('the Rules page deploys with If-Match, lists problems, runs the playground, compares and restores versions, and so do the MCP tools', async ({ request, rulesAccount: { current, page, audit } }) => {
  const database = `${api}/v1/projects/${current.projectId}/databases/${current.databaseId}`;
  const table = 'rules_page_' + randomBytes(4).toString('hex');
  const owner = await ownerToken(request, current);
  const headers = (extra: Record<string, string> = {}) => ({ authorization: 'Bearer ' + owner, ...extra });
  const execute = async (sql: string) => {
    const response = await request.post(database + '/sql/execute', { headers: headers(), data: { statements: [{ sql }] } });
    expect(response.status(), sql).toBe(200);
  };
  const live = async () => {
    const response = await request.get(database + '/rules', { headers: headers() });
    return response.status() === 404 ? null : await answer<RulesBody>(response, 200);
  };
  const owned = { read: 'row.owner_id = auth.uid', columns: ['id', 'owner_id', 'title'] };
  const closed = { read: 'FALSE' };

  await execute(`CREATE TABLE ${table} (id INTEGER PRIMARY KEY, owner_id TEXT NOT NULL, title TEXT NOT NULL, secret TEXT)`);
  try {
    await page.goto(`/databases/${current.databaseId}/rules`);
    await expect(page).toHaveTitle(`${current.databaseName} · Rules · TerraScale`);
    const before = await live();
    expect(JSON.parse(await page.locator('#rules-source').inputValue())).toEqual({ tables: before?.tables ?? {} });
    await audit.check('rules page with the current rules');

    await setRules(page, { tables: { [table]: { read: 'row.missing = auth.uid', columns: ['id', 'nope'] } } });
    await page.getByRole('button', { name: 'Deploy rules', exact: true }).click();
    await expect(page.locator('#rules-error')).toHaveText('The rules were not deployed.');
    const problems = page.locator('#rules-problem-list tbody tr');
    await expect(problems).toHaveCount(2);
    await expect(problems.nth(0)).toHaveAttribute('data-code', 'unknown_column');
    await expect(problems.nth(0).locator('td').nth(0)).toHaveText(table);
    await expect(problems.nth(0).locator('td').nth(1)).toHaveText('columns');
    await expect(problems.nth(1).locator('td').nth(1)).toHaveText('read');
    expect((await live())?.version).toBe(before?.version);
    await audit.check('rules deploy problems listed per table and field');

    const racing = await answer<RulesBody>(await request.put(database + '/rules', {
      headers: headers(before ? { 'if-match': `"${before.version}"` } : { 'if-none-match': '*' }), data: { tables: { [table]: closed } },
    }), 200);
    await setRules(page, { tables: { [table]: owned } });
    await page.getByRole('button', { name: 'Deploy rules', exact: true }).click();
    await expect(page.locator('#rules-error')).toHaveText('These rules changed since you loaded them.');
    expect((await live())?.version).toBe(racing.version);
    await audit.check('rules changed since they were loaded');
    await page.getByRole('link', { name: 'Reload rules', exact: true }).click();
    await expect(page.locator('#rules-source')).toHaveValue(/"read": "FALSE"/);

    await setRules(page, { tables: { [table]: owned } });
    await page.getByRole('button', { name: 'Deploy rules', exact: true }).click();
    await expect(page.locator('#rules-notice')).toHaveText('Rules deployed.');
    const deployed = (await live())!;
    expect(deployed.version).not.toBe(racing.version);
    expect(deployed.tables).toEqual({ [table]: owned });
    const history = page.locator('#rules-versions li');
    await expect(history.first()).toHaveAttribute('data-version', deployed.version);
    await expect(history.first()).toContainText('Current');
    await expect(history.nth(1)).toHaveAttribute('data-version', racing.version);
    await expect(history.first().locator('.ts-rows-meta')).toHaveText(current.email);

    await playground(page, 'read', table, { uid: 'alice', claims: {}, is_anonymous: false }, { id: 1, owner_id: 'alice', title: 'a', secret: 's' });
    await expect(page.locator('#rules-allowed')).toHaveText('Allowed');
    await expect(page.locator('#rules-decided-by')).toHaveText('read');
    await expect(page.locator('#rules-clause')).toHaveText(owned.read);
    await expect(page.locator('#rules-columns')).toHaveText('id, owner_id, title');
    await audit.check('rules playground allows the owner');
    await playground(page, 'read', table, { uid: 'bob', claims: {}, is_anonymous: false }, { id: 1, owner_id: 'alice', title: 'a', secret: 's' });
    await expect(page.locator('#rules-allowed')).toHaveText('Denied');
    await expect(page.locator('#rules-clause')).toHaveText(owned.read);
    await audit.check('rules playground denies another user');
    await setRules(page, { tables: { [table]: { read: 'TRUE' } } });
    await page.getByRole('button', { name: 'Run test', exact: true }).click();
    await expect(page.locator('#rules-allowed')).toHaveText('Allowed');
    await expect(page.locator('#rules-clause')).toHaveText('TRUE');
    expect((await live())?.version).toBe(deployed.version);

    await history.nth(1).getByRole('link', { name: 'Compare', exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`[?&]compare=${racing.version}(&|$)`));
    await expect(page.locator('#rules-diff [data-kind="removed"]').filter({ hasText: '"read": "FALSE"' })).toHaveCount(1);
    await expect(page.locator('#rules-diff [data-kind="added"]').filter({ hasText: `"read": "${owned.read}"` })).toHaveCount(1);
    await audit.check('rules version diff against the current rules');

    await page.locator('#rules-diff').getByRole('button', { name: 'Restore', exact: true }).click();
    const dialog = page.locator('dialog[open]');
    await expect(dialog).toContainText('They replace the current rules for every end user.');
    await audit.check('rules restore confirmation');
    await dialog.getByRole('button', { name: 'Restore version', exact: true }).click();
    await expect(page).toHaveURL(/[?&]restored=1(&|$)/);
    await expect(page.locator('#rules-notice')).toHaveText('Version restored.');
    await expect(page.locator('#rules-source')).toHaveValue(/"read": "FALSE"/);
    const restored = (await live())!;
    expect(restored.tables).toEqual({ [table]: closed });
    await expect(history.first()).toHaveAttribute('data-version', restored.version);
    console.log('Rules page: problems listed, 412 reload, deploy, playground allowed and denied, diff and restore ' + JSON.stringify({ deployed: deployed.version, restored: restored.version }));

    const scope = { databaseId: current.databaseId, organizationId: current.organizationId, projectId: current.projectId };
    const read = await mcpTool(request, current.apiKey, 'get_database_rules', scope);
    expect(read).toMatchObject({ version: restored.version, rules: { tables: { [table]: closed } }, problems: [] });
    const versions = await mcpTool(request, current.apiKey, 'list_database_rules_versions', scope);
    expect(versions.versions.slice(0, 3).map((item: { version: string }) => item.version)).toEqual([restored.version, deployed.version, racing.version]);
    const tested = await mcpTool(request, current.apiKey, 'test_database_rules', {
      ...scope, operation: 'read', table, auth: { uid: 'alice' }, row: { id: 1, owner_id: 'alice', title: 'a' }, rules: { tables: { [table]: owned } },
    });
    expect(tested).toMatchObject({ allowed: true, decidedBy: 'read', clause: owned.read, columns: owned.columns, problems: [] });
    const deniedByDeployed = await mcpTool(request, current.apiKey, 'test_database_rules', { ...scope, operation: 'read', table, auth: { uid: 'alice' }, row: { owner_id: 'alice' } });
    expect(deniedByDeployed).toMatchObject({ allowed: false, decidedBy: 'read', clause: 'FALSE' });
    const refused = await mcpTool(request, current.apiKey, 'deploy_database_rules', { ...scope, version: restored.version, rules: { tables: { [table]: { read: 'new.title = 1' } } } });
    expect(refused).toMatchObject({ deployed: false, version: null, problems: [{ table, field: 'read', code: 'reference_not_allowed' }] });
    await mcpTool(request, current.apiKey, 'deploy_database_rules', { ...scope, version: deployed.version, rules: { tables: { [table]: owned } } }, 'The rules changed since that version.');
    const back = await mcpTool(request, current.apiKey, 'restore_database_rules_version', { ...scope, version: deployed.version, currentVersion: restored.version });
    expect(back).toMatchObject({ deployed: true, problems: [] });
    expect((await live())).toMatchObject({ version: back.version, tables: { [table]: owned } });
    const cleared = await mcpTool(request, current.apiKey, 'deploy_database_rules', { ...scope, version: back.version, rules: { tables: {} } });
    expect(cleared).toMatchObject({ deployed: true, problems: [] });
  } finally {
    await execute(`DROP TABLE IF EXISTS ${table}`);
  }
});
