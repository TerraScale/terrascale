import { createServer } from 'node:http';
import { once } from 'node:events';
import { holdSync } from '../support/ports';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { createHash, randomBytes } from 'node:crypto';
import { type APIResponse } from '@playwright/test';
import { auditedContext, expect, test } from '../support/audit';
import { signInToken, stepUp } from '../support/auth-management';
import { hostedReady } from '../support/hosted';
import { newAddress, nextMail } from '../support/inbox';
import { createDedicatedProjectOwner } from '../support/project-owner';
import { enterSecret } from '../support/secret';
import { auth, desktop } from '../support/stack';
import { saveCookies, saveSecret } from '../support/state';
import { createTsAuthClient } from '../../sdk/js/src/ts-auth.js';

const api = 'https://api.terrascale.tech';
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

const browserTest = test.extend<{ site: { origin: string; example: (source: string) => void } }>({
  site: async ({}, use) => {
    const lease = holdSync({ browser_app: 8050 });
    let example = '';
    const server = createServer((request, response) => {
      const path = new URL(request.url ?? '/', 'http://localhost').pathname;
      const file = path === '/browser.mjs' ? resolve(__dirname, '../sdk/js/browser.mjs') :
        path === '/ulid.js' ? resolve(__dirname, '../../sdk/js/node_modules/ulid/dist/browser/index.js') :
        path.startsWith('/sdk/src/') ? resolve(__dirname, '../../sdk/js', path.slice(5)) : undefined;
      response.setHeader('Content-Type', file || path === '/readme.mjs' ? 'text/javascript' : 'text/html');
      response.end(file ? readFileSync(file) : path === '/readme.mjs' ? example :
        '<!doctype html><title>Notes</title><script type="importmap">{"imports":{"ulid":"/ulid.js","@terrascale/sdk":"/sdk/src/index.js"}}</script>');
    });
    try {
      server.listen(lease.ports.browser_app, '127.0.0.1');
      await once(server, 'listening');
      await use({ origin: 'http://127.0.0.1:' + lease.ports.browser_app, example: source => { example = source; } });
    } finally {
      server.closeAllConnections();
      await new Promise<void>((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
      lease.stop();
    }
  },
});

browserTest('a browser app on an allowed origin signs in guests and users with no backend, and guests reach their own rows', async ({ browser, request, site }, info) => {
  const app = site.origin;
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
  const created = await request.post(`${api}/v1/projects/${project}/databases`, { headers: owner, data: { name: 'browser-' + randomBytes(4).toString('hex'), ...(process.env.E2E_REGIONS === '2' ? { regions: ['gru'] } : {}) } });
  expect(created.status(), await created.text()).toBe(201);
  const database = `${api}/v1/projects/${project}/databases/${(await created.json()).id}`;
  const rows = `${database}/tables/notes/rows`;
  const walkthrough = readFileSync(resolve(__dirname, '../../sdk/js/README.md'), 'utf8').split('## Browser apps with Rules')[1];
  const todoSql = /```sql\n([\s\S]*?)```/.exec(walkthrough)![1];
  const todoRules = JSON.parse(/```json\n([\s\S]*?)```/.exec(walkthrough)![1]);
  const sql = await request.post(database + '/sql/execute', { headers: owner, data: { statements: [
    { sql: todoSql },
    { sql: 'CREATE TABLE notes (id INTEGER PRIMARY KEY, owner_id TEXT NOT NULL, title TEXT NOT NULL)' },
    { sql: "INSERT INTO notes (owner_id, title) VALUES ('someone-else', 'not yours')" },
  ] } });
  expect(sql.status(), await sql.text()).toBe(200);
  const deployed = await request.put(database + '/rules', {
    headers: { ...owner, 'if-none-match': '*' },
    data: { tables: { ...todoRules.tables, notes: {
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

  const example = /```js\n([\s\S]*?)```/.exec(walkthrough)![1];
  const exampleModule = 'const { project, clientId, database } = ' + JSON.stringify({ project, clientId: client.client_id, database: database.split('/').at(-1) }) + ';\n'
    + example + '\nexport { auth, signedIn, created, read, updated };';
  site.example(exampleModule);
  const context = await auditedContext(browser, { viewport: desktop, storageState: { cookies: [], origins: [] } });
  await context.route(url => url.origin === app, route => route.continue());
  const tokenReads: Promise<void>[] = [];
  try {
    const browserApp = await context.newPage();

    context.on('requestfinished', request => {
      if (request.method() !== 'POST' || !request.url().startsWith(auth + '/' + project + '/oauth/') ||
          !/\/(token|anonymous)$/.test(request.url())) return;
      tokenReads.push((async () => {
        const response = await request.response();
        if (response?.status() !== 200) return;
        const body = await response.json();
        for (const name of ['access_token', 'refresh_token', 'id_token'])
          if (typeof body[name] === 'string') saveSecret('sdk-browser-' + name, body[name]);
      })());
    });
    browserApp.on('framenavigated', frame => {
      if (frame !== browserApp.mainFrame()) return;
      const code = new URL(frame.url()).searchParams.get('code');
      if (code) saveSecret('sdk-browser-pkce-code', code);
    });
    await browserApp.goto(app + '/');
    const sdkConfig = { project, clientId: client.client_id, authBaseUrl: auth, redirectUri: redirect,
      database: database.split('/').at(-1)!, baseUrl: api };
    const sdk = (method: string) => browserApp.evaluate(async method => {
      const module = await import(location.origin + '/browser.mjs');
      return module[method]();
    }, method);
    const configureSdk = () => browserApp.evaluate(async config => {
      const module = await import(location.origin + '/browser.mjs');
      module.configure(config);
    }, sdkConfig);
    await configureSdk();
    const proof = await sdk('guestProof');
    expect(proof.user.is_anonymous).toBe(true);
    expect(await browserApp.evaluate(() => Object.keys(sessionStorage))).toEqual([]);
    expect(proof.second.sub).not.toBe(proof.user.sub);
    expect(proof.observed).toEqual([expect.objectContaining({ title: 'SDK guest note', owner_id: proof.user.sub })]);
    expect(proof.read.row).toEqual({ owner_id: proof.user.sub, title: 'SDK guest note' });
    expect(proof.updated.row.title).toBe('SDK renamed');
    expect(proof.batchSize).toBe(2);
    expect(proof.denied).toMatchObject({ ok: false, status: 404, error: { code: 'row_not_found' } });
    expect(proof.forbidden).toMatchObject({ ok: false, status: 403, error: { code: 'column_not_writable', details: { column: 'owner_id' } } });
    expect(proof.rolledBack).toMatchObject({ ok: false, status: 404, error: { code: 'row_not_found', details: { index: 1 } } });
    expect(proof.retained.row.title).toBe('SDK batch update');
    expect(proof.refreshed.sub).toBe(proof.user.sub);
    console.log('Browser SDK: guest sign-in, own create/read/update/delete/batch, foreign update 404, refresh and live insert passed');
    if (process.env.E2E_REGIONS === '2') {
      let refused = 0;
      context.on('response', response => { if (response.url().startsWith('https://fra.api.terrascale.tech/') && response.status() === 421) refused++; });
      expect(await sdk('routingProof')).toEqual([201, 200, 200, 200, 204]);
      expect(refused).toBe(4);
      console.log('Browser SDK: four row mutations followed explicit FRA 421 wrong_region to GRU exactly once; row read passed');
    }
    expect(await sdk('storageProof')).toMatchObject({ restored: true, cleared: true, cleanup: 'storage_unavailable', revoked: 'invalid_grant' });
    console.log('Browser SDK: replaceable async storage restored a user; cleanup failure still revoked the refresh family');
    expect(await sdk('refreshInterleavingProof')).toEqual({ kept: true, shared: true, lost: 'session_changed', waiter: 'session_changed', cleared: true });
    console.log('Browser SDK: accessToken joined a refresh held in async storage; sign-out during that refresh won');
    await sdk('watchRulesReset');
    const rulesUpdate = await request.put(database + '/rules', {
      headers: { ...owner, 'if-match': deployed.headers().etag },
      data: { tables: { ...todoRules.tables, notes: {
        read: "row.owner_id = auth.uid AND row.title <> 'SDK batch update'", create: 'auth.is_anonymous = 1',
        update: 'row.owner_id = auth.uid', delete: 'row.owner_id = auth.uid',
        columns: ['id', 'owner_id', 'title'], writable: ['title'], set: { create: { owner_id: 'auth.uid' } },
      } } },
    });
    expect(rulesUpdate.status(), await rulesUpdate.text()).toBe(200);
    expect(await sdk('rulesReset')).toEqual([]);
    expect((await sdk('stop')).ok).toBe(true);
    console.log('Browser SDK: live shape refetched after Rules deploy and dropped the hidden row');
    const sample = await browserApp.evaluate(async () => {
      const example = await import(location.origin + '/readme.mjs');
      const signedOut = await example.auth.signOut();
      return { signedIn: example.signedIn, created: example.created, read: example.read, updated: example.updated, signedOut };
    });
    expect(sample.signedIn).toMatchObject({ ok: true, value: { is_anonymous: true } });
    expect(sample.created).toMatchObject({ ok: true, status: 201, value: { row: { title: 'Ship it' } } });
    expect(sample.read).toMatchObject({ ok: true, value: { row: { title: 'Ship it' } } });
    expect(sample.updated).toMatchObject({ ok: true, value: { row: { title: 'Shipped' } } });
    expect(sample.signedOut.ok).toBe(true);
    console.log('README todo schema, Rules and browser sample: guest -> ok; create -> 201; read/update -> 200');
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

    await hosted.close();
    await context.clearCookies();
    await configureSdk();
    await Promise.all([browserApp.waitForURL(url => url.origin === new URL(auth).origin), sdk('beginSignIn')]);
    await hostedReady(browserApp);
    await browserApp.locator('#sign-in-email').fill(email);
    await browserApp.locator('button[formaction*="sign-in/password"]').click();
    await enterSecret(browserApp.locator('#sign-in-password'), password);
    await browserApp.getByRole('button', { name: 'Sign in', exact: true }).click();
    await browserApp.waitForURL(redirect + '?**');
    await configureSdk();
    const signedIn = await sdk('complete');
    expect(signedIn.ok).toBe(true);
    expect(signedIn.value.email).toBe(email);
    expect(new URL(browserApp.url()).searchParams.has('code')).toBe(false);
    expect((await sdk('refresh')).ok).toBe(true);
    expect((await sdk('signOut')).ok).toBe(true);
    expect(await sdk('currentUser')).toBeNull();
    console.log('Browser SDK: S256 PKCE redirect, callback cleanup, current user, refresh and sign-out passed');
    const methods = await page.request.put(auth + '/v1/' + project + '/settings', {
      headers: { Authorization: 'Bearer ' + signIn }, data: { methods: { magic_link: true } },
    });
    expect(methods.status(), await methods.text()).toBe(200);
    const magicEmail = await newAddress('browser-magic-' + randomBytes(5).toString('hex'));
    const imported = await createTsAuthClient({ apiBaseUrl: auth, instanceSlug: 'terrascale' }).projects.importUsers(signIn, project, {
      users: [{ email: magicEmail, email_verified: true }],
    });
    if (!imported.ok) throw new Error(JSON.stringify(imported.error));
    expect(imported.value.errors).toEqual([]);
    await context.clearCookies();
    await browserApp.goto(app + '/');
    await configureSdk();
    await Promise.all([browserApp.waitForURL(url => url.origin === new URL(auth).origin), sdk('beginSignIn')]);
    await hostedReady(browserApp);
    await browserApp.locator('#sign-in-email').fill(magicEmail);
    await browserApp.getByRole('button', { name: 'Email a sign-in link', exact: true }).click();
    await hostedReady(browserApp);
    await expect(browserApp.locator('#sign-in-error')).toHaveCount(0);
    const magicMail = await nextMail(magicEmail, 0);
    const magicLink = magicMail.text.match(/https:\/\/auth\.terrascale\.tech\/ui\/[^\s<>]+\/sign-in\/magic\?token=[^\s<>]+/)?.[0] ?? '';
    expect(magicLink).not.toBe('');
    saveSecret('sdk-browser-magic-link', magicLink);
    await browserApp.goto(magicLink);
    await browserApp.waitForURL(redirect + '?**');
    await configureSdk();
    const linked = await sdk('complete');
    expect(linked).toMatchObject({ ok: true, value: { email: magicEmail } });
    expect((await sdk('signOut')).ok).toBe(true);
    console.log('Browser SDK: hosted magic link completed PKCE sign-in');
    await Promise.all(tokenReads);

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
    await Promise.all(tokenReads);
    await saveCookies(context);
    await context.close();
    const removed = await page.request.delete(clients + '/' + client.client_id, { headers: { Authorization: 'Bearer ' + current.apiKey } });
    expect(removed.status()).toBe(204);
    await account.remove();
  }
});
