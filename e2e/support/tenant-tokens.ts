import { expect, type APIRequestContext } from '@playwright/test';
import { keyId } from './api-key';
import { auth } from './stack';
import { saveSecret } from './state';

const projects = 'https://api.terrascale.tech/v1/projects/';

export async function checkTenantTokens(context: APIRequestContext, project: string, otherProject: string, apiKey: string,
  tokens: { user: string; machine: string }): Promise<void> {
  const exchanged = await context.post(auth + '/terrascale/oauth/token', {
    headers: { Authorization: 'Basic ' + Buffer.from(keyId(apiKey) + ':' + apiKey).toString('base64') },
    form: { grant_type: 'client_credentials', project },
  });
  expect(exchanged.status(), 'exchange the project API key for a project token').toBe(200);
  const projectToken = (await exchanged.json()).access_token as string;
  saveSecret('tenant-tokens-project-token', projectToken);
  const owner = { Authorization: 'Bearer ' + projectToken };
  const tenant = { Authorization: 'Bearer ' + tokens.user };
  const created = await context.post(projects + project + '/databases', { headers: owner, data: { name: 'tenant-tokens' } });
  expect(created.status()).toBe(201);
  const id = (await created.json()).id as string;
  const database = projects + project + '/databases/' + id;
  try {
    const subject = JSON.parse(Buffer.from(tokens.user.split('.')[1], 'base64url').toString()).sub as string;
    const setup = await context.post(database + '/sql/execute', {
      headers: owner,
      data: { statements: [
        { sql: 'CREATE TABLE notes (id INTEGER PRIMARY KEY, owner_id TEXT NOT NULL, body TEXT NOT NULL)' },
        { sql: 'INSERT INTO notes (owner_id, body) VALUES (?, ?), (?, ?)', args: [subject, 'mine', '01ARZ3NDEKTSV4RRFFQ69G5FAV', 'theirs'] },
      ] },
    });
    expect(setup.status(), 'create the granted table').toBe(200);
    const grant = await context.put(database + '/tables/notes/read-grant', { headers: owner, data: { where: 'owner_id = :sub' } });
    expect(grant.status(), 'grant end users their own notes').toBe(200);
    const shape = '/tables/notes/shape?offset=-1';
    const ownerRead = await context.get(database + shape, { headers: owner, maxRedirects: 0 });
    expect(ownerRead.status(), 'the project token reads the shape').toBe(307);
    const wrongAudience = await context.get(database + shape, { headers: tenant, maxRedirects: 0 });
    expect(wrongAudience.status(), 'a tenant token without this project\'s data audience').toBe(401);
    const otherProjectRead = await context.get(projects + otherProject + '/databases/' + id + shape, { headers: tenant, maxRedirects: 0 });
    expect(otherProjectRead.status(), 'a tenant token on another project\'s route').toBe(401);
    const machineRead = await context.get(database + shape, { headers: { Authorization: 'Bearer ' + tokens.machine }, maxRedirects: 0 });
    expect(machineRead.status(), 'a tenant machine-client token for the data audience').toBe(401);
  } finally {
    const removed = await context.delete(database, { headers: owner });
    expect(removed.status()).toBe(204);
  }
}
