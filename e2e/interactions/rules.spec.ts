import { randomBytes } from 'node:crypto';
import { expect, test, type APIRequestContext, type APIResponse } from '@playwright/test';
import { auth } from '../support/stack';
import { journey, saveSecret } from '../support/state';

const api = 'https://api.terrascale.tech';

type Problem = { table: string; field: string; code: string; message: string };
type RulesBody = { version: string; tables: Record<string, Record<string, unknown>>; problems?: Problem[]; warnings?: Problem[]; updatedAt: string; updatedBy: string };

async function ownerToken(request: APIRequestContext): Promise<string> {
  const current = journey();
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

test('rules deploy only after compiling against the schema, keep versions and restore an earlier one', async ({ request }) => {
  const current = journey();
  const database = `${api}/v1/projects/${current.projectId}/databases/${current.databaseId}`;
  const table = 'rules_todos_' + randomBytes(4).toString('hex');
  const owner = await ownerToken(request);
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

    await execute(`DROP TABLE ${table}`);
    const drifted = await answer<RulesBody>(await get(), 200);
    expect(drifted.problems?.map(problem => `${problem.table}:${problem.code}`)).toEqual([`${table}:unknown_table`]);
    await answer(await restore(second.version, { 'if-match': `"${restored.version}"` }), 400, 'invalid_rules');
    await answer(await put({ tables: {} }, { 'if-match': `"${restored.version}"` }), 200);
  } finally {
    await execute(`DROP TABLE IF EXISTS ${table}`);
  }
});
