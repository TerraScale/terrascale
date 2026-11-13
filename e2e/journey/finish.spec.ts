import type { APIRequestContext } from '@playwright/test';
import { deleteAccount } from '../support/account';
import { test, expect } from '../support/remote-database';
import { completeUpload, deleteFile, leftFiles, putReserved, putUnnamed, reserveUpload, stored, unnamedPath, type Reservation } from '../support/files';
import { runOperations, sdks } from '../support/sdk';
import { prepareAuthManagement, closeAuthManagement } from '../support/auth-management';
import { deleteProjectOwner } from '../support/project-owner';
import { checkPasswordFairness } from '../support/password-fairness';
import { shell } from '../support/shell';
import { onLocalStack } from '../support/stack';
import { journey, saveSecret, setupFinished } from '../support/state';

async function placementHeaders(request: APIRequestContext): Promise<{ Authorization: string }> {
  const current = journey();
  const basic = Buffer.from(`${current.apiKey.slice(0, 26)}:${current.apiKey}`).toString('base64');
  const answer = await request.post('https://auth.terrascale.tech/terrascale/oauth/token', {
    headers: { Authorization: `Basic ${basic}` },
    form: { grant_type: 'client_credentials', project: current.projectId },
  });
  expect(answer.status()).toBe(200);
  const token = (await answer.json()).access_token as string;
  saveSecret('placement-project-token', token);
  return { Authorization: `Bearer ${token}` };
}

const sweepPass = 15_000;

test.describe.configure({ mode: 'default' });

test.beforeAll(({}, info) => {
  expect(setupFinished(info), 'setup did not finish').toBe(true);
});

test('delete the dedicated auth feature owner after all auth flows', async ({ browser }, info) => {
  await deleteProjectOwner(browser, info);
});

test('a tenant at its hashing cap leaves other tenants normal sign-in capacity', async ({ browser }) => {
  await checkPasswordFairness(browser);
});

test.describe('SDK group A', () => {
  for (const sdk of sdks) {
    test(`${sdk} SDK: SQL, values, sessions, conflicts and ids on the journey database`, async ({ page }) => {
      const management = await prepareAuthManagement(page, sdk);
      try { await runOperations(sdk, 'a', management); }
      finally { await closeAuthManagement(page, management); }
    });
  }
});

test.describe('database deletion', () => {
  test('placement refuses removing the last region and keeps serving', async ({ request }) => {
    const current = journey();
    const headers = await placementHeaders(request);
    const url = `https://api.terrascale.tech/v1/projects/${current.projectId}/databases/${current.databaseId}`;
    const before = await request.get(url, { headers });
    expect(before.status()).toBe(200);
    const original = await before.json();
    for (const data of [
      { regions: [] },
      { regions: null },
      { regions: ['c18-unserved-region'] },
      { regions: [current.databaseRegion.toLowerCase(), current.databaseRegion.toLowerCase()] },
      { regions: [current.databaseRegion.toLowerCase()], homeRegion: current.databaseRegion.toLowerCase() },
      { regions: [current.databaseRegion.toLowerCase()], replicaRegions: [] },
      { regions: [current.databaseRegion.toLowerCase()], allRegions: false },
    ]) {
      const refused = await request.patch(url, { headers, data });
      expect(refused.status()).toBe(400);
      expect((await refused.json()).error.code).toBe('invalid_request');
    }
    const after = await request.get(url, { headers });
    expect(after.status()).toBe(200);
    expect((await after.json()).regions).toEqual(original.regions);
    const query = await request.post(`${url}/sql/execute`, { headers, data: { statements: [{ sql: 'SELECT 1' }] } });
    expect(query.status()).toBe(200);
  });

  test('a deleted database is unreachable, listed for seven days, and restores its rows', async ({ request }) => {
    const current = journey();
    const headers = await placementHeaders(request);
    const databases = `https://api.terrascale.tech/v1/projects/${current.projectId}/databases`;
    const created = await request.post(databases, { headers, data: { name: `undo-${current.runId}`, ttlSeconds: 60 } });
    expect(created.status()).toBe(201);
    const database = await created.json();
    const url = `${databases}/${database.id}`;
    const deletedUrl = `https://api.terrascale.tech/v1/projects/${current.projectId}/deleted-databases`;
    try {
      const written = await request.post(`${url}/sql/execute`, {
        headers,
        data: { statements: [{ sql: 'CREATE TABLE kept (value TEXT)' }, { sql: "INSERT INTO kept VALUES ('retained')" }] },
      });
      expect(written.status()).toBe(200);
      expect((await request.delete(url, { headers })).status()).toBe(204);
      expect((await request.get(url, { headers })).status()).toBe(404);
      expect((await request.post(`${url}/sql/execute`, { headers, data: { statements: [{ sql: 'SELECT * FROM kept' }] } })).status()).toBe(404);
      const listed = await request.get(`${deletedUrl}?limit=100`, { headers });
      expect(listed.status()).toBe(200);
      const retained = (await listed.json()).items.find((item: { id: string }) => item.id === database.id);
      expect(retained.name).toBe(database.name);
      expect(Date.parse(retained.purgeAt) - Date.parse(retained.deletedAt)).toBe(7 * 24 * 60 * 60 * 1000);
      const restored = await request.post(`${deletedUrl}/${database.id}/restore`, { headers });
      expect(restored.status()).toBe(200);
      expect(await restored.json()).toMatchObject({ id: database.id, name: database.name, expiresAt: null });
      const read = await request.post(`${url}/sql/execute`, { headers, data: { statements: [{ sql: 'SELECT value FROM kept' }] } });
      expect(read.status()).toBe(200);
      expect((await read.json()).results[0].rows).toEqual([['retained']]);
      const after = await request.get(`${deletedUrl}?limit=100`, { headers });
      expect(after.status()).toBe(200);
      expect((await after.json()).items.some((item: { id: string }) => item.id === database.id)).toBe(false);
    } finally {
      await request.delete(url, { headers });
    }
  });

  test('delete the journey database on its danger page by typing its name', async ({ page, audit }) => {
    const current = journey();
    await page.goto(`/databases/${current.databaseId}/danger`);
    await shell(page, current);
    await audit.check('database danger zone');

    await page.locator('main').getByRole('button', { name: 'Delete database', exact: true }).click();
    const dialog = page.locator('dialog[open]');
    await expect(dialog).toBeVisible();
    const confirm = dialog.locator('input[name="confirmName"]');
    const submit = dialog.getByRole('button', { name: 'Delete database', exact: true });
    await confirm.fill(current.databaseName);
    await audit.check('delete database dialog');
    await submit.click();

    await page.waitForURL((url) => url.pathname === '/databases');
    await expect(page.locator('main tr').filter({ hasText: current.databaseName })).toHaveCount(0);
    await audit.check('databases after deletion');
  });
  test('the dashboard creates, renames and deletes a database outside its own region', async ({ remoteDatabase }) => {
    await remoteDatabase();
  });

  test('placement regions create, add and remove a non-writer', async ({ request }) => {
    const current = journey();
    const headers = await placementHeaders(request);
    const databases = `https://api.terrascale.tech/v1/projects/${current.projectId}/databases`;
    const createdIds: string[] = [];
    try {
      const servingResponse = await request.get('https://api.terrascale.tech/v1/regions');
      expect(servingResponse.status()).toBe(200);
      const serving = (await servingResponse.json()).regions as string[];
      const writer = current.databaseRegion.toLowerCase();
      const other = serving.find(region => region !== writer);
      const name = `placement-${current.runId}`;
      const created = await request.post(databases, { headers, data: { name, regions: [writer] } });
      expect(created.status()).toBe(201);
      const database = await created.json();
      createdIds.push(database.id);
      expect(database.regions).toEqual([writer]);
      expect(database.homeRegion).toBe(writer);
      expect(database.replicaRegions).toEqual([]);
      const url = `${databases}/${database.id}`;
      const selected = other ? [writer, other] : [writer];
      const added = await request.patch(url, { headers, data: { regions: selected } });
      expect(added.status()).toBe(200);
      expect((await added.json()).regions).toEqual([...selected].sort());
      const observations = await request.get(`${url}/regions`, { headers });
      expect(observations.status()).toBe(200);
      const status = await observations.json();
      expect(status.regions.map((region: { region: string }) => region.region)).toEqual([...selected].sort());
      expect(status.regions.filter((region: { role: string | null }) => region.role === 'writer').map((region: { region: string }) => region.region)).toEqual([writer]);
      const removed = await request.patch(url, { headers, data: { regions: [writer] } });
      expect(removed.status()).toBe(200);
      expect((await removed.json()).regions).toEqual([writer]);
      const repeated = await request.patch(url, { headers, data: { regions: [writer] } });
      expect(repeated.status()).toBe(200);
      expect((await repeated.json()).regions).toEqual([writer]);
      expect((await request.delete(url, { headers })).status()).toBe(204);
      if (other) {
        const ordered = [writer, other].sort().reverse();
        const two = await request.post(databases, { headers, data: { name, regions: ordered } });
        expect(two.status()).toBe(201);
        const body = await two.json();
        createdIds.push(body.id);
        expect(body.homeRegion).toBe(ordered[0]);
        expect(body.regions).toEqual([writer, other].sort());
        expect((await request.delete(`${databases}/${body.id}`, { headers })).status()).toBe(204);
      }
      const legacy = await request.post(databases, { headers, data: { name, homeRegion: writer, replicaRegions: [] } });
      expect(legacy.status()).toBe(201);
      const body = await legacy.json();
      createdIds.push(body.id);
      expect(body.homeRegion).toBe(writer);
      expect(body.replicaRegions).toEqual([]);
      expect(body.regions).toEqual([writer]);
      expect((await request.delete(`${databases}/${body.id}`, { headers })).status()).toBe(204);
    } finally {
      for (const id of createdIds) await request.delete(`${databases}/${id}`, { headers });
    }
  });
});

test.describe('SDK group B', () => {
  for (const sdk of sdks) {
    test(`${sdk} SDK: agent databases with TTL and scoped keys`, async () => {
      await runOperations(sdk, 'b');
    });
  }
});

test.describe('teardown', () => {
  test('a new display name shows in the shell', async ({ page, audit }) => {
    const current = journey();
    const name = `Renamed ${current.runId}`;
    await page.goto('/profile');
    await page.locator('#displayName').fill(name);
    await page.locator('form[action^="/api/v1/dashboard/profile"]').getByRole('button', { name: 'Save profile', exact: true }).click();
    await page.waitForLoadState('load');
    await shell(page, { ...current, displayName: name });
    await audit.check('profile renamed');
  });

  test('the account is deleted through the profile danger tab, a new sign-in is refused and its files leave the object store', async ({ page, audit, browser }, info) => {
    const content = Buffer.from(`held ${journey().runId}`);
    const files: { live: string; held: Reservation; late: Reservation }[] = [];
    await onLocalStack(async () => {
      const left = leftFiles();
      await page.goto(`/storage?bucket=${left.bucket}`);
      const held = await reserveUpload(page, left.bucket, `held-${journey().runId}.txt`, 'text/plain', content);
      expect(await putReserved(held, content), 'the file to delete is uploaded').toBe(200);
      expect(await completeUpload(page, left.bucket, held.id), 'the file to delete is completed').toBe(204);
      expect(await deleteFile(page, left.bucket, held.id), 'the file is deleted just before the account').toBe(303);
      const late = await reserveUpload(page, left.bucket, `late-${journey().runId}.txt`, 'text/plain', content);
      expect([await stored(left.live), await stored(held.path), await stored(late.path)], 'the bucket holds a live file and a just-deleted file, and nothing for the reserved upload').toEqual([true, true, false]);
      files.push({ live: left.live, held, late });
    });
    const closing = Date.now();
    await deleteAccount(page, audit, browser, info);
    for (const { live, held, late } of files) {
      expect([await stored(live), await stored(held.path)], 'the deleted account left no file in the object store').toEqual([false, false]);
      expect(await putReserved(late, content), 'an upload link issued before the deletion still takes its file').toBe(200);
      await expect.poll(() => stored(late.path), { message: 'a file uploaded after the deletion is erased again' }).toBe(false);
      const unnamed = unnamedPath(late.path);
      expect(await putUnnamed(unnamed, content), 'an object that no record names arrives under the deleted project').toBe(200);
      const nextSweep = Date.now() - closing + sweepPass;
      info.setTimeout(info.timeout + nextSweep);
      await expect.poll(() => stored(unnamed), { message: 'the sweep of the project prefix erases an object that no record names', timeout: nextSweep }).toBe(false);
    }
  });
});
