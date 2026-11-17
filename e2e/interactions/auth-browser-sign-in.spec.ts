import { createHash, randomBytes } from 'node:crypto';
import { type APIResponse } from '@playwright/test';
import { auditedContext, expect, test } from '../support/audit';
import { signInToken, stepUp } from '../support/auth-management';
import { hostedReady } from '../support/hosted';
import { newAddress, nextMail } from '../support/inbox';
import { createDedicatedProjectOwner } from '../support/project-owner';
import { enterSecret } from '../support/secret';
import { auth, desktop } from '../support/stack';
import { saveSecret } from '../support/state';

const api = 'https://api.terrascale.tech';
const app = 'https://app.journey.terrascale.tech';
const unlisted = 'https://other.journey.terrascale.tech';

type Tokens = { access_token: string; refresh_token: string; id_token: string };
type Fetched = { status: number; body: Record<string, unknown> | null };

const claims = (token: string) => JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString()) as Record<string, unknown>;

function cors(label: string, response: APIResponse) {
  const headers = response.headers();
  console.log(`${label} -> ${response.status()} access-control-allow-origin=${headers['access-control-allow-origin'] ?? '(none)'}`
    + ` access-control-allow-methods=${headers['access-control-allow-methods'] ?? '(none)'} access-control-allow-headers=${headers['access-control-allow-headers'] ?? '(none)'}`
    + ` access-control-allow-credentials=${headers['access-control-allow-credentials'] ?? '(none)'}`);
  return headers;
}

test('a browser app on an allowed origin signs in guests and users with no backend, and guests reach their own rows', async ({ browser, request }, info) => {
  const account = await createDedicatedProjectOwner(browser, info, 'Browser app owner');
  const { current, page } = account;
  const project = current.projectId;
  const redirect = app + '/callback';
  const dataAudience = api + '/v1/projects/' + project;
  const exchanged = await request.post(auth + '/terrascale/oauth/token', {
    form: { grant_type: 'client_credentials', client_id: current.apiKey.slice(0, 26), client_secret: current.apiKey, project,
      scope: 'sql:read sql:write databases:read databases:write' },
  });
  expect(exchanged.status()).toBe(200);
  const ownerToken = (await exchanged.json()).access_token as string;
  saveSecret('browser-owner-token', ownerToken);
  const owner = { authorization: 'Bearer ' + ownerToken };
  const created = await request.post(`${api}/v1/projects/${project}/databases`, { headers: owner, data: { name: 'browser-' + randomBytes(4).toString('hex') } });
  expect(created.status(), await created.text()).toBe(201);
  const database = `${api}/v1/projects/${project}/databases/${(await created.json()).id}`;
  const rows = `${database}/tables/notes/rows`;
  const sql = await request.post(database + '/sql/execute', { headers: owner, data: { statements: [
    { sql: 'CREATE TABLE notes (id INTEGER PRIMARY KEY, owner_id TEXT NOT NULL, title TEXT NOT NULL)' },
    { sql: "INSERT INTO notes (owner_id, title) VALUES ('someone-else', 'not yours')" },
  ] } });
  expect(sql.status(), await sql.text()).toBe(200);
  const deployed = await request.put(database + '/rules', {
    headers: { ...owner, 'if-none-match': '*' },
    data: { tables: { notes: {
      read: 'row.owner_id = auth.uid', create: 'auth.is_anonymous = 1', update: 'row.owner_id = auth.uid', delete: 'row.owner_id = auth.uid',
      columns: ['id', 'owner_id', 'title'], writable: ['title'], set: { create: { owner_id: 'auth.uid' } },
    } } },
  });
  expect(deployed.status(), await deployed.text()).toBe(200);

  const signIn = await signInToken(page.request, 'browser-owner');
  const clients = auth + '/v1/' + project + '/clients';
  const manage = async (method: 'post' | 'put', url: string, action: string, data: Record<string, unknown>) =>
    page.request[method](url, { headers: { Authorization: 'Bearer ' + signIn, 'X-TerraScale-Step-Up-Proof': await stepUp(page.request, signIn, action, current.password) }, data });
  for (const origin of [app + '/', 'https://*.journey.terrascale.tech', app + '/path', 'HTTPS://APP.JOURNEY.TERRASCALE.TECH', app + ':443', 'http://app.journey.terrascale.tech']) {
    const refused = await manage('post', clients, 'create_oauth_client', { name: 'Refused', type: 'public', redirect_uris: [redirect], allowed_origins: [origin] });
    expect(refused.status(), origin).toBe(400);
  }
  const registered = await manage('post', clients, 'create_oauth_client',
    { name: 'Browser app', type: 'public', redirect_uris: [redirect], allowed_origins: [app], allow_anonymous: true });
  expect(registered.status(), await registered.text()).toBe(201);
  const client = await registered.json() as { client_id: string; allowed_origins: string[]; allow_anonymous: boolean };
  expect(client).toMatchObject({ allowed_origins: [app], allow_anonymous: true });

  const context = await auditedContext(browser, { viewport: desktop, storageState: { cookies: [], origins: [] } });
  try {
    await context.route((url) => url.origin === app, (route) => route.fulfill({ contentType: 'text/html', body: '<!doctype html><title>Notes</title>' }));
    const browserApp = await context.newPage();
    await browserApp.goto(app + '/');
    const fetched = (path: string, init: { method: string; form?: Record<string, string>; bearer?: string }) =>
      browserApp.evaluate(async ({ url, init }) => {
        const response = await fetch(url, {
          method: init.method,
          headers: init.bearer ? { Authorization: 'Bearer ' + init.bearer } : undefined,
          body: init.form ? new URLSearchParams(init.form) : undefined,
        });
        const text = await response.text();
        return { status: response.status, body: text ? JSON.parse(text) as Record<string, unknown> : null };
      }, { url: auth + '/' + project + path, init }) as Promise<Fetched>;

    const guest = await fetched('/oauth/anonymous', { method: 'POST', form: { client_id: client.client_id } });
    console.log(`browser POST /{project}/oauth/anonymous from ${app} -> ${guest.status}`);
    expect(guest.status).toBe(200);
    const guestTokens = guest.body as Tokens;
    saveSecret('browser-guest-access', guestTokens.access_token);
    saveSecret('browser-guest-refresh', guestTokens.refresh_token);
    saveSecret('browser-guest-id', guestTokens.id_token);
    const guestAccess = claims(guestTokens.access_token);
    expect(guestAccess).toMatchObject({ is_anonymous: true, token_use: 'user', iss: auth + '/' + project });
    expect(guestAccess.aud).toEqual(expect.arrayContaining([client.client_id, dataAudience]));
    expect(guestAccess).not.toHaveProperty('email');
    expect(claims(guestTokens.id_token)).toMatchObject({ is_anonymous: true, sub: guestAccess.sub });

    const userinfo = await fetched('/oauth/userinfo', { method: 'GET', bearer: guestTokens.access_token });
    console.log(`browser GET /{project}/oauth/userinfo (preflighted) from ${app} -> ${userinfo.status}`);
    expect(userinfo).toEqual({ status: 200, body: { sub: guestAccess.sub, is_anonymous: true } });

    const guestRefresh = await fetched('/oauth/token', { method: 'POST', form: { grant_type: 'refresh_token', refresh_token: guestTokens.refresh_token, client_id: client.client_id } });
    console.log(`browser POST /{project}/oauth/token refresh_token (guest) from ${app} -> ${guestRefresh.status}`);
    expect(guestRefresh.status).toBe(200);
    const refreshedGuest = guestRefresh.body as Tokens;
    saveSecret('browser-guest-access-refreshed', refreshedGuest.access_token);
    saveSecret('browser-guest-refresh-rotated', refreshedGuest.refresh_token);
    expect(claims(refreshedGuest.access_token)).toMatchObject({ sub: guestAccess.sub, is_anonymous: true });

    const asGuest = { authorization: 'Bearer ' + refreshedGuest.access_token };
    const note = await request.post(rows, { headers: asGuest, data: { row: { title: 'guest note' } } });
    console.log(`guest POST …/tables/notes/rows -> ${note.status()}`);
    expect(note.status(), await note.text()).toBe(201);
    const own = await note.json() as { key: string; row: Record<string, unknown> };
    expect(own.row).toEqual({ id: Number(own.key), owner_id: guestAccess.sub, title: 'guest note' });
    const renamed = await request.patch(`${rows}/${own.key}`, { headers: asGuest, data: { row: { title: 'renamed' } } });
    expect(renamed.status(), await renamed.text()).toBe(200);
    expect((await renamed.json()).row).toEqual({ id: Number(own.key), owner_id: guestAccess.sub, title: 'renamed' });
    const foreign = await request.patch(`${rows}/1`, { headers: asGuest, data: { row: { title: 'taken' } } });
    console.log(`guest PATCH …/tables/notes/rows/{someone else's} -> ${foreign.status()}`);
    expect(foreign.status()).toBe(404);

    const hosted = await context.newPage();
    const email = await newAddress('browser-app-' + randomBytes(5).toString('hex'));
    const password = randomBytes(24).toString('base64url');
    saveSecret('browser-app-user-password', password);
    await hosted.goto(auth + '/ui/' + project + '/sign-up');
    await hostedReady(hosted);
    await hosted.locator('#sign-up-email').fill(email);
    await enterSecret(hosted.locator('#sign-up-password'), password);
    await hosted.locator('form:has(#sign-up-email) button[type=submit]').click();
    const code = /^\s*(\d{6})\s*$/m.exec((await nextMail(email, 0)).text)?.[1];
    expect(code).toMatch(/^\d{6}$/);
    await enterSecret(hosted.locator('#sign-up-code-digit'), code!);
    await hosted.getByRole('button', { name: 'Verify email' }).click();
    await expect(hosted.locator('[data-state=success]')).toContainText('signed in');
    const verifier = randomBytes(32).toString('base64url');
    const authorized = await hosted.request.get(auth + '/' + project + '/oauth/authorize?' + new URLSearchParams({
      client_id: client.client_id, redirect_uri: redirect, response_type: 'code', scope: 'openid email offline_access', state: 'browser-app',
      code_challenge: createHash('sha256').update(verifier).digest('base64url'), code_challenge_method: 'S256',
    }), { maxRedirects: 0 });
    expect(authorized.status()).toBe(302);
    const exchange = await fetched('/oauth/token', { method: 'POST', form: {
      grant_type: 'authorization_code', code: new URL(authorized.headers().location).searchParams.get('code')!, client_id: client.client_id,
      redirect_uri: redirect, code_verifier: verifier,
    } });
    console.log(`browser POST /{project}/oauth/token authorization_code+PKCE from ${app} -> ${exchange.status}`);
    expect(exchange.status).toBe(200);
    const userTokens = exchange.body as Tokens;
    saveSecret('browser-user-access', userTokens.access_token);
    saveSecret('browser-user-refresh', userTokens.refresh_token);
    saveSecret('browser-user-id', userTokens.id_token);
    expect(claims(userTokens.access_token)).not.toHaveProperty('is_anonymous');
    const userRefresh = await fetched('/oauth/token', { method: 'POST', form: { grant_type: 'refresh_token', refresh_token: userTokens.refresh_token, client_id: client.client_id } });
    console.log(`browser POST /{project}/oauth/token refresh_token (user) from ${app} -> ${userRefresh.status}`);
    expect(userRefresh.status).toBe(200);
    saveSecret('browser-user-access-refreshed', (userRefresh.body as Tokens).access_token);
    saveSecret('browser-user-refresh-rotated', (userRefresh.body as Tokens).refresh_token);
    const userNote = await request.post(rows, { headers: { authorization: 'Bearer ' + (userRefresh.body as Tokens).access_token }, data: { row: { title: 'member note' } } });
    expect(userNote.status()).toBe(403);
    const revoked = await fetched('/oauth/revoke', { method: 'POST', form: { token: (userRefresh.body as Tokens).refresh_token, client_id: client.client_id } });
    console.log(`browser POST /{project}/oauth/revoke from ${app} -> ${revoked.status}`);
    expect(revoked.status).toBe(200);

    const preflight = (origin: string, path: string, method: string) => request.fetch(auth + '/' + project + path, {
      method: 'OPTIONS', headers: { Origin: origin, 'Access-Control-Request-Method': method, 'Access-Control-Request-Headers': 'authorization, content-type' },
    });
    const allowed = cors(`OPTIONS /{project}/oauth/token Origin: ${app}`, await preflight(app, '/oauth/token', 'POST'));
    expect(allowed).toMatchObject({ 'access-control-allow-origin': app, 'access-control-allow-methods': 'POST', 'access-control-allow-headers': 'Authorization, Content-Type' });
    expect(allowed).not.toHaveProperty('access-control-allow-credentials');
    expect(cors(`OPTIONS /{project}/oauth/userinfo Origin: ${app}`, await preflight(app, '/oauth/userinfo', 'GET'))['access-control-allow-methods']).toBe('GET');
    const listed = cors(`GET /{project}/oauth/userinfo Origin: ${app}`,
      await request.get(auth + '/' + project + '/oauth/userinfo', { headers: { Origin: app, Authorization: 'Bearer ' + refreshedGuest.access_token } }));
    expect(listed['access-control-allow-origin']).toBe(app);
    expect(cors(`OPTIONS /{project}/oauth/token Origin: ${unlisted}`, await preflight(unlisted, '/oauth/token', 'POST'))).not.toHaveProperty('access-control-allow-origin');
    const foreignOrigin = await request.get(auth + '/' + project + '/oauth/userinfo', { headers: { Origin: unlisted, Authorization: 'Bearer ' + refreshedGuest.access_token } });
    expect(cors(`GET /{project}/oauth/userinfo Origin: ${unlisted}`, foreignOrigin)).not.toHaveProperty('access-control-allow-origin');
    expect(foreignOrigin.status()).toBe(200);
    const management = await request.fetch(clients, { method: 'OPTIONS', headers: { Origin: app, 'Access-Control-Request-Method': 'POST' } });
    expect(cors(`OPTIONS /v1/{project}/clients Origin: ${app}`, management)).not.toHaveProperty('access-control-allow-origin');

    const updated = await manage('put', clients + '/' + client.client_id, 'update_oauth_client',
      { name: 'Browser app', redirect_uris: [redirect], allowed_origins: [], allow_anonymous: false });
    expect(updated.status(), await updated.text()).toBe(200);
    expect(await updated.json()).toMatchObject({ allowed_origins: [], allow_anonymous: false });
    expect(cors(`OPTIONS /{project}/oauth/token Origin: ${app} after removal`, await preflight(app, '/oauth/token', 'POST'))).not.toHaveProperty('access-control-allow-origin');
    const closed = await request.post(auth + '/' + project + '/oauth/anonymous', { form: { client_id: client.client_id } });
    console.log(`POST /{project}/oauth/anonymous after allow_anonymous=false -> ${closed.status()} ${(await closed.json()).error}`);
    expect(closed.status()).toBe(400);
    expect((await closed.json()).error).toBe('unauthorized_client');
  } finally {
    await context.close();
    const removed = await page.request.delete(clients + '/' + client.client_id, { headers: { Authorization: 'Bearer ' + current.apiKey } });
    expect(removed.status()).toBe(204);
    await account.remove();
  }
});
