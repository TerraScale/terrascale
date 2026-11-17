import { randomBytes } from 'node:crypto';
import { expect, test, type APIRequestContext } from '@playwright/test';
import { signInToken } from '../support/auth-management';
import { auth } from '../support/stack';
import { journey, saveSecret } from '../support/state';

const api = 'https://api.terrascale.tech';
const crockford = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';

type ShapeRow = { id: number; owner: string; body: string; secret?: string };
type ShapeBody = { handle: string; next_offset: string; changes: { ops: { op: string; row: ShapeRow }[] }[] };

function ulid(): string {
  return '01' + Array.from(randomBytes(24), byte => crockford[byte % 32]).join('');
}

async function projectToken(request: APIRequestContext): Promise<string> {
  const current = journey();
  const response = await request.post(auth + '/terrascale/oauth/token', {
    form: {
      grant_type: 'client_credentials', client_id: current.apiKey.slice(0, 26), client_secret: current.apiKey,
      project: current.projectId, scope: 'sql:read sql:write databases:write',
    },
  });
  expect(response.status()).toBe(200);
  const credential = (await response.json()).access_token as string;
  saveSecret('read-grant-project-token', credential);
  return credential;
}

async function mintKey(request: APIRequestContext): Promise<string> {
  const current = journey();
  const token = await signInToken(request, 'read-grant-owner');
  const response = await request.post(`${auth}/v1/terrascale/organizations/${current.organizationId}/projects/${current.projectId}/api-keys`, {
    headers: { authorization: 'Bearer ' + token }, data: { name: 'read grant mint', scopes: ['auth:tokens:mint'] },
  });
  expect(response.status()).toBe(201);
  const key = (await response.json()).key as string;
  saveSecret('read-grant-mint-key', key);
  return key;
}

async function endUserToken(request: APIRequestContext, key: string, user: string, table: string): Promise<string> {
  const current = journey();
  const response = await request.post(`${auth}/v1/terrascale/projects/${current.projectId}/end-user-tokens`, {
    headers: { authorization: 'Bearer ' + key },
    data: { user_id: user, database: current.databaseId, tables: [table] },
  });
  expect(response.status()).toBe(200);
  const credential = (await response.json()).access_token as string;
  saveSecret('read-grant-end-user-' + user, credential);
  return credential;
}

test('end-user shape reads see only granted columns and their own rows, under handles bound to the user and the grant', async ({ request, page }) => {
  const current = journey();
  const database = `${api}/v1/projects/${current.projectId}/databases/${current.databaseId}`;
  const table = 'grant_notes_' + randomBytes(4).toString('hex');
  const shape = `${database}/tables/${table}/shape`;
  const grant = `${database}/tables/${table}/read-grant`;
  const owner = await projectToken(request);
  const execute = async (sql: string) => {
    const response = await request.post(database + '/sql/execute', {
      headers: { authorization: 'Bearer ' + owner }, data: { statements: [{ sql }] },
    });
    expect(response.status(), sql).toBe(200);
  };
  const putGrant = (columns: string[]) => request.put(grant, {
    headers: { authorization: 'Bearer ' + owner }, data: { where: 'owner = :sub', columns },
  });
  const [alice, bob] = [ulid(), ulid()];

  await execute(`CREATE TABLE ${table} (id INTEGER PRIMARY KEY, owner TEXT NOT NULL, body TEXT NOT NULL, secret TEXT NOT NULL)`);
  try {
    await execute(`INSERT INTO ${table} (id, owner, body, secret) VALUES (1, '${alice}', 'a1', 'pin-1111'), (2, '${alice}', 'a2', 'pin-2222'), (3, '${bob}', 'b1', 'pin-3333')`);

    const duplicate = await putGrant(['id', 'owner', 'body', 'ID']);
    expect(duplicate.status()).toBe(400);
    expect((await duplicate.json()).error.code).toBe('duplicate_column');
    expect((await putGrant(['id', 'owner', 'body'])).status()).toBe(200);

    const mint = await mintKey(page.request);
    const aliceToken = await endUserToken(request, mint, alice, table);
    const bobToken = await endUserToken(request, mint, bob, table);
    const read = (credential: string, query: string, maxRedirects = 20) =>
      request.get(`${shape}?${query}`, { headers: { authorization: 'Bearer ' + credential }, maxRedirects });

    for (const where of ["secret = 'pin-1111'", "length(secret) > 0", "missing_column = 1"]) {
      const hidden = await read(aliceToken, 'offset=-1&where=' + encodeURIComponent(where));
      expect(hidden.status(), where).toBe(403);
      expect((await hidden.json()).error.code, where).toBe('column_not_granted');
    }

    const bootstrap = async (credential: string) => {
      const redirect = await read(credential, 'offset=-1', 0);
      expect(redirect.status(), await redirect.text()).toBe(307);
      const handle = redirect.headers()['tb-shape-handle'];
      const page = await request.get(api + redirect.headers()['location'], { headers: { authorization: 'Bearer ' + credential } });
      expect(page.status()).toBe(200);
      const body = await page.json() as ShapeBody;
      expect(body.handle).toBe(handle);
      return { handle, body, rows: body.changes.flatMap(change => change.ops.map(op => op.row)) };
    };
    const aliceShape = await bootstrap(aliceToken);
    const bobShape = await bootstrap(bobToken);
    expect(aliceShape.handle).not.toBe(bobShape.handle);
    expect(aliceShape.rows).toEqual([{ id: 1, owner: alice, body: 'a1' }, { id: 2, owner: alice, body: 'a2' }]);
    expect(bobShape.rows).toEqual([{ id: 3, owner: bob, body: 'b1' }]);

    expect((await putGrant(['id', 'owner', 'body'])).status()).toBe(200);
    const stale = await read(aliceToken, `handle=${aliceShape.handle}&offset=${aliceShape.body.next_offset}`);
    expect(stale.status()).toBe(409);
    const refetch = await stale.json();
    expect(refetch.error.code).toBe('must_refetch');
    expect(refetch.error.handle).not.toBe(aliceShape.handle);
    expect(stale.headers()['tb-shape-handle']).toBe(refetch.error.handle);
    expect((await request.delete(grant, { headers: { authorization: 'Bearer ' + owner } })).status()).toBe(204);
  } finally {
    await execute(`DROP TABLE ${table}`);
  }
});
