import { test, expect, type APIRequestContext, type Page, type Response } from '@playwright/test';
import type { Audit } from './audit';
import { auth, dash } from './stack';
import { journey, saveSecret } from './state';

export async function manageRemoteDatabase(page: Page, request: APIRequestContext, audit: Audit): Promise<void> {
  const discovery = await request.get('https://api.terrascale.tech/v1/regions');
  expect(discovery.status()).toBe(200);
  const serving = (await discovery.json()).regions as string[];
  test.skip(serving.length === 1, 'discovery lists one region');
  const readiness = await request.get(dash + '/readyz');
  expect(readiness.status()).toBe(200);
  const own = (await readiness.json()).region as string;
  expect(own, 'the dashboard reports its region in /readyz').toBeTruthy();
  const region = serving.find(region => region.toLowerCase() !== own.toLowerCase());
  expect(region, 'discovery lists a region outside the dashboard').toBeTruthy();
  const current = journey();
  const name = 'remote-' + current.runId;
  const renamed = 'renamed-' + name;
  let id = '';
  let deleted = false;
  let submitted = false;
  const failures: Response[] = [];
  const failed = (response: Response) => {
    if (response.url().startsWith(dash + '/') && [503, 504].includes(response.status())) failures.push(response);
  };
  page.on('response', failed);

  async function answered(response: Response | null): Promise<void> {
    expect(response, 'the dashboard answers the navigation').not.toBeNull();
    if (response!.status() === 503 || response!.status() === 504) {
      const body = await response!.text();
      const code = /"code"\s*:\s*"([^"]+)"/.exec(body)?.[1] ?? String(response!.status());
      throw new Error('the dashboard in ' + own + ' cannot reach TerraBase in ' + region + ': ' + code);
    }
    expect(response!.status(), 'the dashboard operation succeeds').toBeLessThan(400);
  }

  async function healthy(label: string): Promise<void> {
    if (failures.length) await answered(failures[0]);
    await expect(page.locator('[role="alert"], .ts-alert-error, .ts-field-error'), label + ' shows no error alert').toHaveCount(0);
    await audit.check(label);
  }

  async function navigate(path: string): Promise<void> {
    await answered(await page.goto(path));
  }

  async function submit(action: string, click: () => Promise<void>): Promise<void> {
    const answer = page.waitForResponse(response => response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/v1/dashboard/databases/' + action);
    await click();
    const response = await answer;
    if (action === 'create') id = /\/databases\/([0-9A-HJKMNP-TV-Z]{26})/.exec(response.headers().location ?? '')?.[1] ?? '';
    await answered(response);
    expect(response.status(), action + ' redirects after saving').toBe(303);
    await page.waitForLoadState('load');
    if (failures.length) await answered(failures[0]);
  }

  async function removeInDashboard(): Promise<void> {
    await navigate('/databases/' + id + '/danger');
    await healthy('remote database danger zone');
    const actualName = await page.locator('.ts-db-head .ts-db-title').innerText();
    await page.locator('main').getByRole('button', { name: 'Delete database', exact: true }).click();
    const dialog = page.locator('dialog[open]');
    await expect(dialog).toBeVisible();
    await dialog.locator('input[name="confirmName"]').fill(actualName);
    await healthy('delete remote database dialog');
    await submit('delete', () => dialog.getByRole('button', { name: 'Delete database', exact: true }).click());
    await page.waitForURL(url => url.pathname === '/databases');
    await expect(page.locator('main a[href="/databases/' + id + '"]')).toHaveCount(0);
    await expect(page.locator('main tr').filter({ hasText: name })).toHaveCount(0);
    await expect(page.locator('main tr').filter({ hasText: renamed })).toHaveCount(0);
    await healthy('remote database gone from the list');
    deleted = true;
  }

  async function removeThroughApi(): Promise<void> {
    const token = await request.post(auth + '/terrascale/oauth/token', {
      headers: { Authorization: 'Basic ' + Buffer.from(current.apiKey.slice(0, 26) + ':' + current.apiKey).toString('base64') },
      form: { grant_type: 'client_credentials', project: current.projectId },
    });
    expect(token.status()).toBe(200);
    const accessToken = (await token.json()).access_token as string;
    saveSecret('remote-database-cleanup-token', accessToken);
    const headers = { Authorization: 'Bearer ' + accessToken };
    const path = '/v1/projects/' + current.projectId + '/databases';
    const origin = 'https://' + region + '.api.terrascale.tech';
    if (!id) {
      const listed = await request.get(origin + path, { headers });
      expect(listed.status()).toBe(200);
      const databases = (await listed.json()).items as { id: string; name: string }[];
      id = databases.find(database => database.name === name || database.name === renamed)?.id ?? '';
    }
    if (!id) return;
    let removed = await request.delete(origin + path + '/' + id, { headers });
    if (removed.status() === 421) {
      expect((await removed.json()).error.code).toBe('wrong_region');
      removed = await request.delete('https://' + removed.headers()['x-terrabase-database-home'] + '.api.terrascale.tech' + path + '/' + id, { headers });
    }
    expect([204, 404], 'the regional API leaves no temporary database').toContain(removed.status());
  }

  try {
    await navigate('/databases');
    await page.getByRole('button', { name: 'New database', exact: true }).first().click();
    await expect(page.locator('#create-database[open]')).toBeVisible();
    const options = page.locator('#create-database-regions input[name="regions"]');
    await page.locator('#create-database-regions input[name="regions"][value="' + region + '" i]').check();
    for (const option of await options.all()) {
      if ((await option.inputValue()).toLowerCase() !== region!.toLowerCase()) await option.uncheck();
    }
    await expect(page.locator('#create-database-regions input[name="regions"]:checked')).toHaveValue(new RegExp('^' + region + '$', 'i'));
    await page.locator('#database-name').fill(name);
    await healthy('remote database creation dialog');
    submitted = true;
    await submit('create', () => page.locator('#create-database form').getByRole('button', { name: 'Create database', exact: true }).click());
    expect(id, 'creation returns the remote database id').toBeTruthy();
    await page.waitForURL(url => url.pathname === '/databases/' + id);
    await expect(page.locator('.ts-db-head .ts-db-title')).toHaveText(name);
    await expect(page.locator('.ts-db-head .ts-db-region')).toHaveAttribute('data-region', region!.toLowerCase());
    await healthy('database created outside the dashboard region');
    await navigate('/databases/' + id + '/replication');
    await expect(page.locator('#database-regions input[name="regions"]:checked')).toHaveValue(new RegExp('^' + region + '$', 'i'));
    await expect(page.locator('.ts-db-head .ts-db-region')).toHaveAttribute('data-region', region!.toLowerCase());
    await healthy('remote database replication');
    await navigate('/databases/' + id + '/settings');
    await healthy('remote database settings');
    await page.locator('#rename-name').fill(renamed);
    await page.locator('#rename-confirm').fill(name);
    await submit('rename', () => page.getByRole('button', { name: 'Rename database', exact: true }).click());
    await expect(page.locator('.ts-db-head .ts-db-title')).toHaveText(renamed);
    await healthy('remote database renamed');
    await answered(await page.reload());
    await expect(page.locator('.ts-db-head .ts-db-title')).toHaveText(renamed);
    await healthy('remote database name persists after reload');
    await removeInDashboard();
  } catch (error) {
    if (submitted && !deleted) {
      try {
        if (id) await removeInDashboard();
        if (!deleted) await removeThroughApi();
      } catch (dashboardCleanup) {
        try {
          await removeThroughApi();
        } catch (apiCleanup) {
          throw new AggregateError([error, dashboardCleanup, apiCleanup], 'remote database flow and cleanup failed');
        }
      }
    }
    throw error;
  } finally {
    page.off('response', failed);
  }
}
