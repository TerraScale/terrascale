import { randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { readFileSync } from 'node:fs';
import { connect } from 'node:http2';
import { join } from 'node:path';
import { expect } from '@playwright/test';
import { auth, localPort, production, stateDir } from '../support/stack';
import { keyId } from '../support/api-key';
import { signInToken } from '../support/auth-management';
import { request as addressedRequest } from '../support/client-address';
import { journey, ownerSession, saveSecret } from '../support/state';
import { test, gru, project, database, name, json, removeDatabase, type Database } from './support';

test.skip(production || process.env.E2E_REGIONS !== '2', 'catalog guards drive the local stack started with E2E_REGIONS=2');

const platformOrganization = '00000000000000000000000001';
const authProject = '00000000000000000000000002';

type Reply = { status: number; body: string };

function ulid(): string {
  const alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
  return Array.from(randomBytes(26), byte => alphabet[byte % 32]).join('').replace(/^./, '0');
}

async function operator(method: string, path: string, payload?: unknown, user?: string): Promise<Reply> {
  const session = connect(`http://127.0.0.1:${localPort('internal', 8081)}`);
  const stream = session.request({
    ':method': method, ':path': path, 'x-terrabase-peer-credential': readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[0],
    ...(payload === undefined ? {} : { 'content-type': 'application/json' }),
    ...(user ? { 'x-terrabase-user-id': user } : {}),
  });
  session.on('error', error => stream.destroy(error));
  try {
    const response = once(stream, 'response');
    stream.end(payload === undefined ? undefined : JSON.stringify(payload));
    const [headers] = await response;
    let body = '';
    for await (const chunk of stream) body += chunk;
    return { status: Number(headers[':status']), body };
  } finally {
    session.close();
  }
}

async function expectStatus<T>(reply: Reply, status: number): Promise<T> {
  expect(reply.status, reply.body).toBe(status);
  return (reply.body ? JSON.parse(reply.body) : {}) as T;
}

async function issue(secretName: string, body: { organizationId: string; projectId?: string; databaseId?: string; scopes: string[] }): Promise<{ id: string; secret: string }> {
  const issued = await expectStatus<{ id: string; secret: string }>(await operator('POST', '/admin/api-keys', { name: secretName, ...body }), 201);
  saveSecret(secretName, issued.secret);
  return issued;
}

const bearer = (secret: string) => ({ authorization: `Bearer ${secret}` });

test('a customer key minted on a TerraScale platform project reaches none of its databases', async ({ request }) => {
  const key = await issue('catalog-guard-platform-key', {
    organizationId: platformOrganization, projectId: authProject, scopes: ['sql:read', 'databases:read'],
  });
  try {
    const headers = bearer(key.secret);
    const listed = await request.get(`${gru}${project(authProject)}/databases`, { headers });
    expect(listed.status(), 'listing platform databases').toBe(404);
    expect(await listed.text()).not.toContain('ts-auth');
    const described = await request.get(`${gru}${database('ts-auth', authProject)}`, { headers });
    expect(described.status(), 'reading the ts-auth database').toBe(404);
    const read = await request.post(`${gru}${database('ts-auth', authProject)}/sql/execute`, { headers, data: { statements: [{ sql: 'SELECT 1' }] } });
    expect(read.status(), 'SQL on the ts-auth database').toBe(404);
  } finally {
    await expectStatus(await operator('DELETE', `/admin/api-keys/${key.id}`), 204);
  }
});

test('a token narrowed to one database creates, ensures, branches and restores no other database', async ({ cells, request }) => {
  const current = journey();
  const narrowed = await json<Database>(await cells.post(`${gru}${project()}/databases`, { data: { name: name('narrowed-own') } }), 201);
  const fresh = name('narrowed-fresh');
  const platformProject = `${auth}/v1/terrascale/organizations/${current.organizationId}/projects/${current.projectId}`;
  const owner = await addressedRequest.newContext({ storageState: ownerSession() });
  let ownerHeaders: Record<string, string> = {};
  let key: string | undefined;
  try {
    ownerHeaders = { Authorization: `Bearer ${await signInToken(owner, 'catalog-guard-owner')}` };
    const created = await owner.post(`${platformProject}/api-keys`, {
      headers: ownerHeaders,
      data: { name: name('narrowed-key'), scopes: ['sql:read', 'sql:write', 'databases:read', 'databases:write'], database: narrowed.id },
    });
    expect(created.status(), await created.text()).toBe(201);
    key = (await created.json() as { key: string }).key;
    saveSecret('catalog-guard-narrowed-key', key);
    const granted = await request.post(`${auth}/terrascale/oauth/token`, {
      form: {
        grant_type: 'client_credentials', client_id: keyId(key), client_secret: key,
        project: current.projectId, scope: 'sql:read sql:write databases:read databases:write',
      },
    });
    expect(granted.status(), await granted.text()).toBe(200);
    const token = (await granted.json() as { access_token: string }).access_token;
    saveSecret('catalog-guard-narrowed-token', token);
    const headers = bearer(token);
    const own = await request.post(`${gru}${database(narrowed.id)}/sql/execute`, { headers, data: { statements: [{ sql: 'SELECT 1' }] } });
    expect(own.status(), 'SQL on its own database').toBe(200);
    for (const [label, response] of [
      ['ensuring a new database', await request.put(`${gru}${database(fresh)}`, { headers })],
      ['creating a new database', await request.post(`${gru}${project()}/databases`, { headers, data: { name: fresh } })],
      ['branching into a new database', await request.post(`${gru}${database(narrowed.id)}/branches`, { headers, data: { name: fresh } })],
      ['restoring into a new database', await request.post(`${gru}${database(narrowed.id)}/point-in-time/restore`, {
        headers, data: { name: fresh, pointInTime: new Date().toISOString() },
      })],
    ] as const) {
      const text = await response.text();
      expect(response.status(), `${label}: ${text}`).toBe(403);
      expect((JSON.parse(text) as { error: { code: string } }).error.code, label).toBe('database_narrowed');
    }
    expect((await cells.get(`${gru}${database(fresh)}`)).status(), 'the refused calls created nothing').toBe(404);
  } finally {
    if (key) expect((await owner.delete(`${platformProject}/api-keys/${keyId(key)}`, { headers: ownerHeaders })).status(), 'revoking the narrowed key').toBe(204);
    await owner.dispose();
    await removeDatabase(cells, narrowed.id);
  }
});

test('a Hobby database keeps backups only as long as its plan allows', async () => {
  const user = ulid();
  const organization = await expectStatus<{ id: string; plan: string }>(
    await operator('POST', '/v1/management/organizations', { name: name('retention') }, user), 201);
  expect(organization.plan).toBe('hobby');
  const organizationPath = `/v1/management/organizations/${organization.id}`;
  const created = await expectStatus<{ id: string }>(await operator('POST', `${organizationPath}/projects`, { name: name('retention') }, user), 201);
  const hobby = await expectStatus<Database>(await operator('POST', `${project(created.id)}/databases`, { name: name('retention') }, user), 201);
  try {
    const policy = `${database(hobby.id, created.id)}/backup-policy`;
    const tooLong = await expectStatus<{ error: { code: string } }>(await operator('PUT', policy, { retentionDays: 3650 }, user), 400);
    expect(tooLong.error.code).toBe('invalid_retention');
    const weekly = await expectStatus<{ error: { code: string } }>(await operator('PUT', policy, { retentionDays: 7 }, user), 400);
    expect(weekly.error.code).toBe('invalid_retention');
    const allowed = await expectStatus<{ retentionDays: number }>(await operator('PUT', policy, { retentionDays: 1 }, user), 200);
    expect(allowed.retentionDays).toBe(1);
    const read = await expectStatus<{ retentionDays: number }>(await operator('GET', policy, undefined, user), 200);
    expect(read.retentionDays).toBe(1);
    const second = await expectStatus<{ error: { code: string } }>(await operator('POST', `${project(created.id)}/databases`, { name: name('retention-second') }, user), 409);
    expect(second.error.code).toBe('database_limit');
    const branch = await expectStatus<{ error: { code: string } }>(
      await operator('POST', `${database(hobby.id, created.id)}/branches`, { name: name('retention-branch') }, user), 409);
    expect(branch.error.code).toBe('database_limit');
  } finally {
    await expectStatus(await operator('DELETE', database(hobby.id, created.id), undefined, user), 204);
    await expectStatus(await operator('DELETE', `${organizationPath}/projects/${created.id}`, undefined, user), 204);
    await expectStatus(await operator('DELETE', organizationPath, undefined, user), 204);
  }
});
