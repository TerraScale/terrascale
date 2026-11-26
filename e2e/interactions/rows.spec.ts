import { createHash, randomBytes } from 'node:crypto';
import { type APIResponse, type Browser } from '@playwright/test';
import { auditedContext, expect, test } from '../support/audit';
import { signInToken, stepUp } from '../support/auth-management';
import { hostedReady } from '../support/hosted';
import { newAddress, nextMail } from '../support/inbox';
import { enterSecret } from '../support/secret';
import { auth, dash, desktop } from '../support/stack';
import { createDedicatedProjectOwner } from '../support/project-owner';
import { saveSecret } from '../support/state';

const api = 'https://api.terrascale.tech';

type Answer = { key: string; row: Record<string, unknown> | null };
type Refusal = { error: { code: string; message: string; details?: Record<string, unknown> } };

async function tenantUser(browser: Browser, project: string, client: string, redirect: string, label: string) {
  const context = await auditedContext(browser, { viewport: desktop, storageState: { cookies: [], origins: [] } });
  try {
    const page = await context.newPage();
    const email = await newAddress('rows-' + label + '-' + randomBytes(5).toString('hex'));
    const password = randomBytes(24).toString('base64url');
    saveSecret('rows-' + label + '-password', password);
    await page.goto(auth + '/ui/' + project + '/sign-up');
    await hostedReady(page);
    await page.locator('#sign-up-email').fill(email);
    await enterSecret(page.locator('#sign-up-password'), password);
    await page.locator('form:has(#sign-up-email) button[type=submit]').click();
    const code = /^\s*(\d{6})\s*$/m.exec((await nextMail(email, 0)).text)?.[1];
    expect(code).toMatch(/^\d{6}$/);
    await enterSecret(page.locator('#sign-up-code-digit'), code!);
    await page.getByRole('button', { name: 'Verify email' }).click();
    await expect(page.locator('[data-state=success]')).toContainText('signed in');
    const verifier = randomBytes(32).toString('base64url');
    const authorized = await page.request.get(auth + '/' + project + '/oauth/authorize?' + new URLSearchParams({
      client_id: client, redirect_uri: redirect, response_type: 'code', scope: 'openid email', state: 'rows-' + label,
      code_challenge: createHash('sha256').update(verifier).digest('base64url'), code_challenge_method: 'S256',
    }), { maxRedirects: 0 });
    expect(authorized.status()).toBe(302);
    const issued = await page.request.post(auth + '/' + project + '/oauth/token', {
      form: { grant_type: 'authorization_code', code: new URL(authorized.headers().location).searchParams.get('code')!, client_id: client,
        redirect_uri: redirect, code_verifier: verifier },
    });
    expect(issued.status()).toBe(200);
    const token = (await issued.json()).access_token as string;
    saveSecret('rows-' + label + '-access', token);
    return { token, uid: JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString()).sub as string };
  } finally {
    await context.close();
  }
}

async function answered<T>(method: string, response: APIResponse, status: number, code?: string): Promise<T> {
  const text = await response.text();
  console.log(`${method} ${new URL(response.url()).pathname.replace(/\/v1\/projects\/[^/]+\/databases\/[^/]+/, '…')} -> ${response.status()}${code ? ' ' + code : ''}`);
  expect(response.status(), text).toBe(status);
  const body = text ? JSON.parse(text) : null;
  if (code) expect((body as Refusal).error.code).toBe(code);
  return body as T;
}

test('end users write rows only as the database rules allow, checked in the home write transaction', async ({ browser, request }, info) => {
  const account = await createDedicatedProjectOwner(browser, info, 'Rows owner');
  const { current, page } = account;
  const project = current.projectId;
  const redirect = dash + '/auth/rows';
  const exchanged = await request.post(auth + '/terrascale/oauth/token', {
    form: { grant_type: 'client_credentials', client_id: current.apiKey.slice(0, 26), client_secret: current.apiKey, project,
      scope: 'sql:read sql:write databases:read databases:write' },
  });
  expect(exchanged.status()).toBe(200);
  const ownerToken = (await exchanged.json()).access_token as string;
  saveSecret('rows-owner-token', ownerToken);
  const owner = { authorization: 'Bearer ' + ownerToken };
  const databaseName = 'rows-' + randomBytes(4).toString('hex');
  const created = await request.post(`${api}/v1/projects/${project}/databases?name=${databaseName}-query`, {
    headers: owner, data: { name: databaseName, project: '00000000000000000000000000' },
  });
  expect(created.status(), 'the route project wins over the body: ' + await created.text()).toBe(201);
  const createdBody = await created.json() as { id: string; projectId: string; name: string };
  expect(createdBody.name, 'the JSON body wins over the query string').toBe(databaseName);
  expect(createdBody.projectId, 'the route project wins over the body').toBe(project);
  const database = `${api}/v1/projects/${project}/databases/${createdBody.id}`;
  const table = 'todos';
  const rows = `${database}/tables/${table}/rows`;
  const signIn = await signInToken(page.request, 'rows-owner');
  let client = '';
  try {
    const clients = await page.request.post(auth + '/v1/' + project + '/clients', {
      headers: { Authorization: 'Bearer ' + signIn, 'X-TerraScale-Step-Up-Proof': await stepUp(page.request, signIn, 'create_oauth_client', current.password) },
      data: { name: 'Rows journey', type: 'public', redirect_uris: [redirect] },
    });
    expect(clients.status()).toBe(201);
    client = (await clients.json()).client_id as string;
    const alice = await tenantUser(browser, project, client, redirect, 'alice');
    const bob = await tenantUser(browser, project, client, redirect, 'bob');
    const as = (user: { token: string }) => ({ authorization: 'Bearer ' + user.token });
    const sql = async (statement: string) => {
      const response = await request.post(database + '/sql/execute', { headers: owner, data: { statements: [{ sql: statement }] } });
      expect(response.status(), statement).toBe(200);
      return (await response.json()).results[0].rows as unknown[][];
    };

    await sql(`CREATE TABLE ${table} (id INTEGER PRIMARY KEY, owner_id TEXT NOT NULL, title TEXT NOT NULL, done INTEGER NOT NULL DEFAULT 0, secret TEXT, created_at INTEGER)`);
    await answered('POST', await request.post(rows, { headers: as(alice), data: { row: { title: 'before rules' } } }), 403, 'rule_denied');

    const deployed = await request.put(database + '/rules', {
      headers: { ...owner, 'if-none-match': '*' },
      data: { tables: { [table]: {
        read: 'row.owner_id = auth.uid',
        create: 'auth.is_anonymous = 0',
        update: 'row.owner_id = auth.uid AND new.owner_id = row.owner_id',
        delete: 'row.owner_id = auth.uid',
        columns: ['id', 'owner_id', 'title', 'done'],
        writable: ['title', 'done'],
        validate: ['length(new.title) BETWEEN 1 AND 200', 'new.done IN (0, 1)'],
        set: { create: { owner_id: 'auth.uid', created_at: 'request.time' } },
      } } },
    });
    expect(deployed.status(), await deployed.text()).toBe(200);

    const first = await answered<Answer>('POST', await request.post(rows, { headers: as(alice), data: { row: { title: 'milk' } } }), 201);
    expect(first.row).toEqual({ id: Number(first.key), owner_id: alice.uid, title: 'milk', done: 0 });
    const stored = await sql(`SELECT owner_id, created_at > 0 FROM ${table} WHERE id = ${first.key}`);
    expect(stored).toEqual([[alice.uid, 1]]);

    for (const [row, status, code, column] of [
      [{ title: 'x', owner_id: bob.uid }, 403, 'column_not_writable', 'owner_id'],
      [{ title: 'x', secret: 'pin' }, 403, 'column_not_writable', 'secret'],
      [{ title: 'x', nope: 1 }, 400, 'unknown_column', 'nope'],
    ] as const) {
      const refused = await answered<Refusal>('POST', await request.post(rows, { headers: as(alice), data: { row } }), status, code);
      expect(refused.error.details?.column).toBe(column);
    }

    await answered('PATCH', await request.patch(`${rows}/${first.key}`, { headers: as(bob), data: { row: { title: 'taken' } } }), 404, 'row_not_found');
    await answered('DELETE', await request.delete(`${rows}/${first.key}`, { headers: as(bob) }), 404, 'row_not_found');
    const invalid = await answered<Refusal>('PATCH', await request.patch(`${rows}/${first.key}`, { headers: as(alice), data: { row: { title: '' } } }), 400, 'validation_failed');
    expect(invalid.error.details?.clause).toBe(0);
    const outOfRange = await answered<Refusal>('POST', await request.post(rows, { headers: as(alice), data: { row: { title: 'bad', done: 5 } } }), 400, 'validation_failed');
    expect(outOfRange.error.details?.clause).toBe(1);
    expect(await sql(`SELECT title FROM ${table} ORDER BY id`)).toEqual([['milk']]);

    const updated = await answered<Answer>('PATCH', await request.patch(`${rows}/${first.key}`, { headers: as(alice), data: { row: { done: true } } }), 200);
    expect(updated.row).toMatchObject({ title: 'milk', done: 1 });
    const read = await answered<Answer>('GET', await request.get(`${rows}/${first.key}`, { headers: as(alice) }), 200);
    expect(read.row).toEqual(updated.row);
    await answered('GET', await request.get(`${rows}/${first.key}`, { headers: as(bob) }), 404, 'row_not_found');

    const batch = (user: { token: string }, operations: unknown[]) => request.post(database + '/rows:batch', { headers: as(user), data: { operations } });
    const rolledBack = await answered<Refusal>('POST', await batch(alice, [
      { op: 'create', table, row: { title: 'eggs' } },
      { op: 'update', table, key: first.key, row: { title: 'oat milk' } },
      { op: 'create', table, row: { title: '' } },
    ]), 400, 'validation_failed');
    expect(rolledBack.error.details).toMatchObject({ index: 2, clause: 0 });
    await answered<Refusal>('POST', await batch(bob, [{ op: 'delete', table, key: first.key }]), 404, 'row_not_found');
    expect(await sql(`SELECT title FROM ${table} ORDER BY id`)).toEqual([['milk']]);

    const applied = await answered<{ results: Answer[] }>('POST', await batch(alice, [
      { op: 'create', table, row: { title: 'eggs' } },
      { op: 'update', table, key: first.key, row: { title: 'oat milk' } },
    ]), 200);
    expect(applied.results.map(result => result.row?.title)).toEqual(['eggs', 'oat milk']);
    const bobs = await answered<Answer>('POST', await request.post(rows, { headers: as(bob), data: { row: { title: 'bread' } } }), 201);
    expect(bobs.row?.owner_id).toBe(bob.uid);

    await answered('DELETE', await request.delete(`${rows}/${applied.results[0].key}`, { headers: as(alice) }), 204);
    await answered('DELETE', await request.delete(`${rows}/${applied.results[0].key}`, { headers: as(alice) }), 404, 'row_not_found');
    const full = await answered<Answer>('POST', await request.post(rows, { headers: owner, data: { row: { owner_id: 'service', title: 'audit', secret: 'pin' } } }), 201);
    expect(full.row).toMatchObject({ owner_id: 'service', secret: 'pin', created_at: null });
    expect(await sql(`SELECT owner_id, title FROM ${table} ORDER BY id`)).toEqual([[alice.uid, 'oat milk'], [bob.uid, 'bread'], ['service', 'audit']]);
  } finally {
    if (client) expect((await page.request.delete(auth + '/v1/' + project + '/clients/' + client, { headers: { Authorization: 'Bearer ' + current.apiKey } })).status()).toBe(204);
    expect((await request.delete(database, { headers: owner })).status()).toBe(204);
    await account.remove();
  }
});
