import { once } from 'node:events';
import { readFileSync } from 'node:fs';
import { connect } from 'node:http2';
import { join } from 'node:path';
import { expect } from '@playwright/test';
import { auth, localPort, production, stateDir } from '../support/stack';
import { journey, ownerSession, saveSecret } from '../support/state';
import { signInToken } from '../support/auth-management';
import { test, gru, fra, project, database, name, json, answer, converged, type Cells, type Database } from './support';

test.skip(production || process.env.E2E_REGIONS !== '2' || process.env.E2E_NODES_PER_REGION !== '1',
  'one node in each of two regions');

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

test('bookmark and point-in-time restores run in fra and gru reads the copies', async ({ cells }) => {
  await publish('pro');
  const source = await json<Database>(await cells.post(`${fra}${project()}/databases`, {
    data: { name: name('backup-fra'), regions: ['fra', 'gru'] },
  }), 201);
  const copies: string[] = [];
  try {
    await answer(cells, fra, source.id, ['CREATE TABLE rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)']);
    const snapshot = await answer(cells, fra, source.id, ["INSERT INTO rows (id, value) VALUES (1, 'snapshot')"]);
    await converged(cells, gru, source.id, 'SELECT 1', rows => rows.length === 1);
    expect((await answer(cells, gru, source.id, ['SELECT id, value FROM rows ORDER BY id'], { bookmark: snapshot.bookmark })).results[0].rows,
      'gru reads the fra write with its bookmark').toEqual([[1, 'snapshot']]);
    const archived = await json<{ databaseId: string; latestRestorePointUtc: string }>(await cells.post(`${fra}${database(source.id)}/backups`));
    expect(archived.databaseId).toBe(source.id);
    const bookmarked = await json<{ operationId: string }>(await cells.post(`${fra}${database(source.id)}/point-in-time/restore`, {
      data: { bookmark: snapshot.bookmark, name: name('snapshot-copy-fra') },
    }), 202);
    copies.push(await completed(cells, source.id, bookmarked.operationId));
    expect((await answer(cells, fra, copies[0], ['SELECT id, value FROM rows ORDER BY id'])).results[0].rows).toEqual([[1, 'snapshot']]);

    await answer(cells, fra, source.id, ["INSERT INTO rows (id, value) VALUES (2, 'later')"]);
    const acked = Date.now();
    let at = 0;
    await expect.poll(async () => {
      const window = await cells.get(`${fra}${database(source.id)}/point-in-time`);
      if (window.status() !== 200) return 0;
      at = Date.parse((await window.json() as { latestRestorePointUtc: string }).latestRestorePointUtc);
      return at;
    }, { message: 'fra proves the later write' }).toBeGreaterThanOrEqual(acked);
    const when = new Date(at).toISOString();
    const timed = await json<{ operationId: string }>(await cells.post(`${fra}${database(source.id)}/point-in-time/restore`, {
      data: { pointInTime: when, name: name('time-copy-fra') },
    }), 202);
    copies.push(await completed(cells, source.id, timed.operationId));
    expect((await answer(cells, fra, copies[1], ['SELECT id, value FROM rows ORDER BY id'])).results[0].rows).toEqual([[1, 'snapshot'], [2, 'later']]);
    await converged(cells, gru, copies[0], 'SELECT id, value FROM rows ORDER BY id', rows => JSON.stringify(rows) === JSON.stringify([[1, 'snapshot']]));
    await converged(cells, gru, copies[1], 'SELECT id, value FROM rows ORDER BY id', rows => JSON.stringify(rows) === JSON.stringify([[1, 'snapshot'], [2, 'later']]));
    console.log(`fra: bookmark ${snapshot.bookmark} and point-in-time ${when} restored in fra; gru reads both copies`);
  } finally {
    for (const id of copies) await discard(cells, id);
    await discard(cells, source.id);
  }
});

async function completed(cells: Cells, source: string, operation: string) {
  let copy = '';
  await expect.poll(async () => {
    const state = await json<{ state: string; targetDatabaseId: string; failureMessage: string | null }>(
      await cells.get(`${fra}${database(source)}/restores/${operation}`));
    expect(state.state, state.failureMessage ?? '').not.toBe('failed');
    if (state.state === 'succeeded') copy = state.targetDatabaseId;
    return state.state;
  }, { message: 'the restore succeeds' }).toBe('succeeded');
  return copy;
}

async function discard(cells: Cells, id: string) {
  await expect.poll(async () => {
    const response = await cells.delete(`${fra}${database(id)}`);
    if (response.status() === 429) return false;
    expect(response.status(), await response.text()).toBe(204);
    return true;
  }, { message: 'delete the database in fra once the organization budget refills' }).toBe(true);
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
