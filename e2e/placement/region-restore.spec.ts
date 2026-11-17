import { once } from 'node:events';
import { randomBytes } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { connect } from 'node:http2';
import { join } from 'node:path';
import { expect, type Page } from '@playwright/test';
import { extendPeers, startRegion, type RegionProcess } from './fleet';
import { auth, localPort, production, stateDir } from '../support/stack';
import { journey, ownerSession, saveSecret } from '../support/state';
import { signInToken } from '../support/auth-management';
import { ulid } from '../support/status-worker';
import { Nats, putKey, seal } from '../replication/push.mjs';
import { test, home, replica, project, database, name, json, sql, removeDatabase, type Cells } from './support';

test.skip(production || process.env.E2E_REGIONS !== '2' || process.env.E2E_NODES_PER_REGION !== '1',
  'one node in each of two regions, with port 8380 free for sjc');

test('a newly recorded conflict appears without reloading or replacing draft filters', async ({ cells, page }) => {
  const current = journey();
  const source = await json<{ id: string }>(await cells.post(`${home}${project()}/databases`, {
    data: { name: name('live-conflicts'), regions: ['gru', 'fra'] },
  }), 201);
  const nats = new Nats(readFileSync(join(stateDir, 'nats-token'), 'utf8'), localPort('nats', 4222));
  try {
    await sql(cells, home, source.id, 'CREATE TABLE live_conflicts (id INTEGER PRIMARY KEY, value TEXT NOT NULL)');
    await sql(cells, home, source.id, "INSERT INTO live_conflicts VALUES (1, 'winning')");
    await page.context().addCookies(JSON.parse(readFileSync(ownerSession(), 'utf8')).cookies);
    await page.goto(`/databases/${source.id}/conflicts?organizationId=${current.organizationId}&projectId=${current.projectId}`);
    const url = page.url();
    await page.locator('html').evaluate(el => el.setAttribute('data-in-place', 'live-conflicts'));
    await page.locator('#conflict-table').fill('draft_table');
    const scope = `${current.organizationId}/${current.projectId}/${source.id}`;
    const secrets = readFileSync(join(stateDir, 'secrets'), 'utf8').trim().split('\n');
    const encryptionId = ulid();
    const key = randomBytes(32);
    saveSecret('live-conflict-key', key.toString('base64'));
    await putKey({ master: Buffer.from(secrets[1], 'base64'), access: secrets[12], secret: secrets[13] }, scope, encryptionId, key);
    await nats.connect();
    const transaction = {
      id: ulid(), region: 'fra', hlc: { physicalMilliseconds: Date.now() - 60_000, logical: 0, nodeId: 'journey-2' },
      changes: [{ op: 'update', table: 'live_conflicts', key: { id: 1 }, values: { value: 'losing' } }],
    };
    const applied = nats.event(`terrabase.applied.fra.${scope.replaceAll('/', '.')}`, (result: { id?: string }) => result.id === transaction.id);
    expect((await nats.request(`terrabase.push.fra.gru.${scope.replaceAll('/', '.')}`, seal(transaction, scope, encryptionId, key))).error).toBeUndefined();
    expect(await applied).toMatchObject({ state: 'confirmed', conflicts: [{ kind: 'superseded' }] });
    await expect(page.locator('#conflict-results tbody')).toContainText('losing');
    await expect(page.locator('#conflict-results tbody')).toContainText('winning');
    await expect(page.locator('#conflict-table')).toHaveValue('draft_table');
    await expect(page.locator('html')).toHaveAttribute('data-in-place', 'live-conflicts');
    await expect(page).toHaveURL(url);
  } finally {
    nats.close();
    await removeDatabase(cells, source.id);
  }
  await expect(page.locator('#conflict-results')).toContainText('We can’t find that page.');
  await expect(page.locator('#conflict-results[data-init]')).toHaveCount(0);
});

test('notification inbox, dialog and both shell counts reflect an API change and another tab marking read', async ({ page }) => {
  const current = journey();
  const streams: string[] = [];
  page.on('request', request => {
    const url = new URL(request.url());
    if (url.pathname === '/notifications/live' && url.searchParams.get('view') === 'shell') streams.push(url.href);
  });
  await page.context().addCookies(JSON.parse(readFileSync(ownerSession(), 'utf8')).cookies);
  await page.goto('/notifications?status=unread');
  const url = page.url();
  await page.locator('html').evaluate(el => el.setAttribute('data-in-place', 'live-inbox'));
  const token = await signInToken(page.request, 'live-notification');
  const keys = `${auth}/v1/terrascale/organizations/${current.organizationId}/projects/${current.projectId}/api-keys`;
  const name = `live-inbox-${current.runId}`;
  const created = await page.request.post(keys, {
    headers: { Authorization: `Bearer ${token}` },
    data: { name, scopes: ['sql:read'], expires_at: new Date(Date.now() + 86400_000).toISOString() },
  });
  expect(created.status()).toBe(201);
  const key = await created.json();
  saveSecret('live-notification-key', key.key);
  try {
    await expect(page.locator('#notification-inbox')).toContainText(`API key ${name} expires`);
    await expect(page.locator('#ts-notifications-topbar')).toHaveAttribute('aria-label', /unread/);
    await expect(page.locator('#ts-notifications-menu')).toHaveAttribute('aria-label', /unread/);
    expect(streams).toHaveLength(1);
    await page.locator('#ts-notifications-topbar').click();
    await expect(page.locator('#ts-notifications-body')).toContainText(`API key ${name} expires`);
    const tab = await page.context().newPage();
    try {
      await tab.goto('/notifications?status=unread');
      await tab.locator('#notification-inbox').getByRole('button', { name: 'Mark all read', exact: true }).click();
      await expect(page.locator('#ts-notifications-body .ts-notification-unread')).toHaveCount(0);
      await expect(page.locator('#notification-inbox')).toContainText('No unread notifications in this scope.');
      await expect(page.locator('#ts-notifications-topbar')).toHaveAttribute('aria-label', 'Notifications');
      await expect(page.locator('#ts-notifications-menu')).toHaveAttribute('aria-label', 'Notifications');
      expect(streams).toHaveLength(1);
      await expect(page.locator('html')).toHaveAttribute('data-in-place', 'live-inbox');
      await expect(page).toHaveURL(url);
    } finally {
      await tab.close();
    }
    const live = await page.locator('#ts-notifications-body').getAttribute('data-init');
    expect(live).toContain('/notifications/live');
    await page.keyboard.press('Escape');
    await expect(page.locator('#ts-notifications-body[data-init]')).toHaveCount(0);
  } finally {
    expect((await page.request.delete(`${keys}/${key.api_key.id}`, { headers: { Authorization: `Bearer ${token}` } })).status()).toBe(204);
  }
});

test('snapshot and point-in-time restore run in fra', async ({ cells, page }) => {
  await publish('pro');
  const otherProject = await liveWorkspace(page);
  await restorePair(cells, replica, 'fra', page, '', otherProject);
});

test('snapshot and point-in-time restore run in sjc', async ({ cells, page }) => {
  const restorePeers = extendPeers(['sjc']);
  let node: RegionProcess | undefined;
  try {
    node = await startRegion('sjc', 0);
    const metrics = `http://${new URL(node.endpoint).hostname}:${node.metricsPort}/metrics`;
    await expect.poll(async () => {
      const response = await cells.get(metrics);
      expect(response.status(), await response.text()).toBe(200);
      return (await response.text()).split('\n').find(line => line.startsWith('terrabase_clock_local_writes_allowed{'));
    }, { message: 'sjc trusts its clock before the source database accepts writes' }).toMatch(/ 1$/);
    await publish('pro');
    await restorePair(cells, node.endpoint, 'sjc', page, node.endpoint);
  } finally {
    if (node) await node.stop();
    restorePeers();
  }
});

async function liveWorkspace(page: Page): Promise<string> {
  await page.context().addCookies(JSON.parse(readFileSync(ownerSession(), 'utf8')).cookies);
  await page.goto('/organization?tab=billing');
  await page.locator('article.ts-plan', { has: page.locator('#plan-pro') }).getByRole('button', { name: 'Upgrade to Pro', exact: true }).click();
  await page.waitForURL(url => url.origin === 'https://checkout.stripe.com');
  await page.getByRole('button', { name: 'Subscribe', exact: true }).click();
  await expect(page.locator('#billing-summary .ts-billing-plan-name')).toHaveText('Pro');
  const csrf = await page.locator('input[name="_csrf"]').first().inputValue();
  expect((await page.request.post('/api/v1/dashboard/organization/projects', {
    form: { _csrf: csrf, name: name('other-workspace'), regions: 'gru' }, maxRedirects: 0,
  })).status()).toBe(303);
  await page.goto('/organization?tab=projects');
  const other = await page.locator('[data-project-option]').evaluateAll((options, current) =>
    options.map(option => option.getAttribute('data-project-option')).find(id => id !== current), journey().projectId);
  expect(other).toBeTruthy();
  return other!;
}

async function restorePair(cells: Cells, endpoint: string, region: string, page: Page, removeEndpoint = '', otherProject = '') {
  const sourceName = name('backup-' + region);
  const streams: string[] = [];
  page.on('request', request => {
    if (new URL(request.url()).pathname.endsWith('/backups/live')) streams.push(request.url());
  });
  const source = await json<{ id: string }>(await cells.post(`${endpoint}${project()}/databases`, {
    data: { name: sourceName, regions: [region, 'gru'] },
  }), 201);
  const copies: string[] = [];
  try {
    await sql(cells, endpoint, source.id, 'CREATE TABLE rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)');
    await sql(cells, endpoint, source.id, "INSERT INTO rows (id, value) VALUES (1, 'snapshot')");
    await served(cells, source.id, [[1, 'snapshot']]);
    const rules = { tables: { rows: { read: 'TRUE', columns: ['id', 'value'] } } };
    const misplaced = await cells.put(`${home}${database(source.id)}/rules`, { headers: { 'if-none-match': '*' }, data: rules });
    expect(misplaced.status(), await misplaced.text()).toBe(421);
    expect((await misplaced.json()).error.code).toBe('wrong_region');
    expect(misplaced.headers()['x-terrabase-database-home']).toBe(region);
    const deployed = await json<{ version: string }>(await cells.put(`${endpoint}${database(source.id)}/rules`, {
      headers: { 'if-none-match': '*' }, data: rules,
    }), 200);
    expect(deployed.version).toMatch(/^[0-9A-HJKMNP-TV-Z]{26}$/);
    const current = journey();
    await page.context().addCookies(JSON.parse(readFileSync(ownerSession(), 'utf8')).cookies);
    await page.goto(`/databases/${source.id}/backups?organizationId=${current.organizationId}&projectId=${current.projectId}`);
    const viewUrl = page.url();
    await page.locator('html').evaluate(el => el.setAttribute('data-in-place', 'live-backups'));
    const backup = await json<{ id: string; status: string }>(await cells.post(`${endpoint}${database(source.id)}/backups`), 201);
    expect(backup.status).toBe('ready');
    await expect(page.locator(`#restore-points-list button[command="show-modal"][commandfor="restore-${backup.id}"]`)).toBeVisible();
    await expect(page).toHaveURL(viewUrl);
    await expect(page.locator('html')).toHaveAttribute('data-in-place', 'live-backups');
    if (otherProject) {
      const csrf = await page.locator('input[name="_csrf"]').first().inputValue();
      expect((await page.request.post('/api/v1/dashboard/switch-project', {
        form: { _csrf: csrf, projectId: otherProject }, maxRedirects: 0,
      })).status()).toBe(303);
      expect((await (await page.request.get('/api/v1/auth/me')).json()).projectId).toBe(otherProject);
    }
    await page.locator(`button[command="show-modal"][commandfor="restore-${backup.id}"]`).click();
    await page.locator(`#restore-${backup.id}-name`).fill(sourceName);
    await page.locator(`#restore-${backup.id}`).getByRole('button', { name: 'Restore as new database', exact: true }).click();
    const validation = page.locator(`#restore-${backup.id}-error`);
    await expect(validation).toBeVisible();
    const message = await validation.innerText();
    await page.locator(`#restore-${backup.id}`).getByRole('button', { name: 'Cancel', exact: true }).click();
    for (let index = 0; index < 10; index++)
      await json(await cells.post(`${endpoint}${database(source.id)}/backups`), 201);
    await expect(page.getByRole('button', { name: 'Show older restore points', exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Show older restore points', exact: true }).click();
    await expect(page.locator(`button[command="show-modal"][commandfor="restore-${backup.id}"]`)).toBeVisible();
    const expanded = await json<{ id: string }>(await cells.post(`${endpoint}${database(source.id)}/backups`), 201);
    await expect(page.locator(`button[command="show-modal"][commandfor="restore-${expanded.id}"]`)).toBeVisible();
    await expect(page.locator(`button[command="show-modal"][commandfor="restore-${backup.id}"]`)).toBeVisible();
    await expect(validation).toHaveText(message);
    await expect(page.locator(`#restore-${backup.id}-name`)).toHaveValue(sourceName);
    const live = new URL(streams.at(-1)!);
    expect(live.searchParams.get('organizationId')).toBe(current.organizationId);
    expect(live.searchParams.get('projectId')).toBe(current.projectId);
    const stopped = page.waitForEvent('requestfailed', request => request.url() === live.href);
    await page.locator('#restore-points').evaluate(el => el.remove());
    await stopped;
    const unavailableRestore = new URL(viewUrl);
    unavailableRestore.searchParams.set('restore', 'unavailable');
    await page.goto(unavailableRestore.href);
    await expect(page.locator('#restore-status [role="alert"]')).toHaveText('That restore is no longer available.');
    await expect(page.locator('#restore-status[data-init]')).toHaveCount(0);
    if (otherProject) {
      const csrf = await page.locator('input[name="_csrf"]').first().inputValue();
      expect((await page.request.post('/api/v1/dashboard/switch-project', {
        form: { _csrf: csrf, projectId: current.projectId }, maxRedirects: 0,
      })).status()).toBe(303);
    }
    const elsewhere = await cells.post(`${home}${database(source.id)}/backups/${backup.id}/restore`, {
      data: { name: name('snapshot-' + region) },
    });
    expect(elsewhere.status(), await elsewhere.text()).toBe(421);
    expect(elsewhere.headers()['ts-replay']).toBe('region=' + region);
    const snapshot = await json<{ id: string }>(await cells.post(`${endpoint}${database(source.id)}/backups/${backup.id}/restore`, {
      data: { name: name('snapshot-copy-' + region) },
    }), 202);
    copies.push(await completed(cells, endpoint, source.id, snapshot.id));
    expect(await rows(cells, endpoint, copies[0])).toEqual([[1, 'snapshot']]);

    await sql(cells, endpoint, source.id, "INSERT INTO rows (id, value) VALUES (2, 'later')");
    const acked = Date.now();
    await served(cells, source.id, [[1, 'snapshot'], [2, 'later']]);
    let at = 0;
    await expect.poll(async () => {
      const window = await cells.get(`${endpoint}${database(source.id)}/point-in-time`);
      if (window.status() !== 200) return 0;
      at = Date.parse((await window.json() as { latest: string }).latest);
      return at;
    }, { message: region + ' proves the later write' }).toBeGreaterThanOrEqual(acked);
    const when = new Date(at).toISOString();
    const missed = await cells.post(`${home}${database(source.id)}/point-in-time/restore`, {
      data: { pointInTime: when, name: name('time-' + region) },
    });
    expect(missed.status(), await missed.text()).toBe(421);
    expect(missed.headers()['ts-replay']).toBe('region=' + region);
    const timed = await json<{ id: string }>(await cells.post(`${endpoint}${database(source.id)}/point-in-time/restore`, {
      data: { pointInTime: when, name: name('time-copy-' + region) },
    }), 202);
    copies.push(await completed(cells, endpoint, source.id, timed.id));
    expect(await rows(cells, endpoint, copies[1])).toEqual([[1, 'snapshot'], [2, 'later']]);
    console.log(`${region}: snapshot ${backup.id} and point-in-time ${when} restored on that region; gru answered 421 region=${region}`);
  } finally {
    for (const id of copies) await remove(cells, removeEndpoint || home, id, !removeEndpoint);
    await remove(cells, removeEndpoint || home, source.id, !removeEndpoint);
    await expect(page.locator('#restore-points')).toContainText('We can’t find that page.');
    await expect(page.locator('#restore-points[data-init]')).toHaveCount(0);
  }
}

async function served(cells: Cells, id: string, expected: (number | string)[][]) {
  await expect.poll(async () => {
    const response = await cells.post(`${home}${database(id)}/sql/execute`, {
      data: { statements: [{ sql: 'SELECT id, value FROM rows ORDER BY id' }] },
    });
    if (response.status() !== 200 || response.headers()['x-terrabase-region'] !== 'gru') return null;
    return (await response.json() as { results: { rows: (number | string)[][] }[] }).results[0].rows;
  }, { message: 'gru holds the placed copy' }).toEqual(expected);
}

async function completed(cells: Cells, endpoint: string, source: string, operation: string) {
  let copy = '';
  await expect.poll(async () => {
    const state = await json<{ status: string; database: string; errorCode?: string }>(
      await cells.get(`${endpoint}${database(source)}/restores/${operation}`));
    expect(state.status, state.errorCode).not.toBe('failed');
    if (state.status === 'completed') copy = state.database;
    return state.status;
  }, { message: 'the restore completes' }).toBe('completed');
  return copy;
}

async function rows(cells: Cells, endpoint: string, id: string) {
  return (await sql(cells, endpoint, id, 'SELECT id, value FROM rows ORDER BY id')).results[0].rows;
}

async function remove(cells: Cells, endpoint: string, id: string, followHome: boolean) {
  if (followHome) {
    await removeDatabase(cells, id);
    return;
  }
  await expect.poll(async () => {
    const response = await cells.delete(`${endpoint}${database(id)}`);
    if (response.status() === 409 || response.status() === 429) return false;
    expect(response.status(), await response.text()).toBe(204);
    return true;
  }, { message: 'delete the database on its home' }).toBe(true);
}

async function publish(plan: string) {
  const credential = readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[0];
  const session = connect('http://127.0.0.1:' + localPort('internal', 8081));
  const stream = session.request({
    ':method': 'PUT', ':path': `/admin/organizations/${journey().organizationId}/plan`,
    'x-terrabase-peer-credential': credential, 'content-type': 'application/json',
  });
  session.on('error', error => stream.destroy(error));
  try {
    const response = once(stream, 'response');
    stream.end(JSON.stringify({ plan }));
    const [headers] = await response;
    let body = '';
    stream.setEncoding('utf8');
    for await (const chunk of stream) body += chunk;
    expect(Number(headers[':status']), body).toBe(200);
  } finally {
    session.close();
  }
}
