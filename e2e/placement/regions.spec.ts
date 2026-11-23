import { randomBytes } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect, type APIResponse, type Page } from '@playwright/test';
import {
  test, authenticatedCells, gru, fra, bookmarkHeader, consistencyHeader, project, database, name, regionOf, json, sql, run, answer, converged,
  regions as replication, replicated, removeDatabase, type Cells, type Database,
} from './support';
import { auth, dash, production, stateDir } from '../support/stack';
import { keyId } from '../support/api-key';
import { Audit, auditedContext } from '../support/audit';
import { journey, ownerSession, saveCookies, saveSecret } from '../support/state';
import { confirmWithPassword, onboard, signUp, verify } from '../support/hosted';
import { newAddress } from '../support/inbox';
import { createApiKey } from '../support/paid';

test.skip(production || process.env.E2E_REGIONS !== '2', 'placement requires the local stack started with E2E_REGIONS=2');
test.describe.configure({ mode: 'default', timeout: 180_000 });

type Refusal = { error: { code: string; message: string; sqlState?: string | null } };
type Catalog = { region: string; machine: string; regions: { id: string; name: string; publicHost: string | null; acceptingWrites: boolean; local: boolean; nodes: number }[] };
type Frame = { type: string; id: number; results?: { rows: unknown[][] }[]; bookmark?: string | null; transaction: boolean; error?: { code: string; message: string; sqlState?: string | null } };

async function refused(response: APIResponse, code: number, documented: string): Promise<Refusal> {
  expect(response.status(), response.status() === code ? documented : `${documented}: ${await response.text()}`).toBe(code);
  const body = await response.json() as Refusal;
  expect(body.error.code, documented).toBe(documented);
  return body;
}

async function create(cells: Cells, regions: string[], projectId?: string, region = gru): Promise<Database> {
  const created = await json<Database>(await cells.post(`${region}${project(projectId)}/databases`, { data: { name: name('regions'), regions } }), 201);
  expect(created.regions).toEqual([...regions].sort());
  return created;
}

async function place(cells: Cells, id: string, regions: string[], projectId?: string, region = gru): Promise<Database> {
  const placed = await json<Database>(await cells.patch(`${region}${database(id, projectId)}`, { data: { regions } }));
  expect(placed.regions).toEqual([...regions].sort());
  return placed;
}

async function seed(cells: Cells, id: string, projectId?: string): Promise<void> {
  await sql(cells, gru, id, 'CREATE TABLE placement_rows (value INTEGER PRIMARY KEY)', projectId);
  await sql(cells, gru, id, 'INSERT INTO placement_rows VALUES (1), (2), (3)', projectId);
}

async function healthy(cells: Cells, region: string, id: string, expected: string[], projectId?: string): Promise<void> {
  const observed = await replication(cells, region, id, projectId);
  expect(observed.regions.map(row => row.region), `${regionOf(region)} lists every region of the database`).toEqual(expected);
  for (const row of observed.regions) {
    expect(row, `${regionOf(region)} reports ${row.region} as an equal peer`).not.toHaveProperty('role');
    expect(row.healthy, `${regionOf(region)} reaches ${row.region}`).toBe(true);
    expect(row.appliedBookmark, `${regionOf(region)} reports the bookmark ${row.region} applied`).not.toBe('');
    expect(row.lagMs).toEqual(expect.any(Number));
  }
}

async function sqlToken(request: Cells): Promise<string> {
  const current = journey();
  const grant = await json<{ access_token: string }>(await request.post(`${auth}/terrascale/oauth/token`, {
    form: { grant_type: 'client_credentials', client_id: keyId(current.apiKey), client_secret: current.apiKey, project: current.projectId, scope: 'sql:read sql:write' },
  }));
  saveSecret('regions-socket-token', grant.access_token);
  return grant.access_token;
}

async function strongRace(page: Page, id: string, token: string): Promise<{ gru: Frame[]; fra: Frame[] }> {
  return page.evaluate(async ({ urls, token }) => {
    const open = (url: string) => new Promise<(frame: object) => Promise<Frame>>((resolve, reject) => {
      const socket = new WebSocket(url, 'terrascale.sql.v2');
      const waiting: ((frame: Frame) => void)[] = [];
      let next = 0;
      socket.onmessage = message => waiting.shift()?.(JSON.parse(String(message.data)) as Frame);
      socket.onerror = () => reject(new Error('the SQL socket failed to open: ' + url));
      socket.onopen = () => resolve(frame => new Promise<Frame>(answered => {
        waiting.push(answered);
        socket.send(JSON.stringify({ id: ++next, ...frame }));
      }));
    });
    const [atGru, atFra] = await Promise.all(urls.map(open));
    const frames = { gru: [] as Frame[], fra: [] as Frame[] };
    const step = async (region: 'gru' | 'fra', frame: object) => {
      frames[region].push(await (region === 'gru' ? atGru : atFra)(frame));
    };
    for (const region of ['gru', 'fra'] as const) await step(region, { type: 'auth', token });
    for (const region of ['gru', 'fra'] as const) await step(region, { type: 'begin', consistency: 'strong' });
    for (const region of ['gru', 'fra'] as const) await step(region, { type: 'execute', statements: [{ sql: 'SELECT value FROM ledger WHERE id = 1' }] });
    await step('gru', { type: 'execute', statements: [{ sql: 'UPDATE ledger SET value = value + 1 WHERE id = 1' }] });
    await step('fra', { type: 'execute', statements: [{ sql: 'UPDATE ledger SET value = value + 10 WHERE id = 1' }] });
    for (const region of ['gru', 'fra'] as const) await step(region, { type: 'commit' });
    return frames;
  }, { urls: [gru, fra].map(origin => origin.replace('https:', 'wss:') + `/v1/projects/${journey().projectId}/databases/${id}/sql/ws`), token });
}

test('R1 create two regions, read across listeners and see every region healthy', async ({ cells, browser }, info) => {
  const created = await create(cells, ['gru', 'fra']);
  try {
    await seed(cells, created.id);
    await replicated(cells, fra, created.id, 3);
    await healthy(cells, gru, created.id, ['fra', 'gru']);
    await healthy(cells, fra, created.id, ['fra', 'gru']);
    const context = await auditedContext(browser, { storageState: ownerSession() });
    const page = await context.newPage();
    const audit = new Audit(page, info);
    await page.goto(`${dash}/databases/${created.id}/backups`);
    await page.locator('button[commandfor=bucket-dialog][command=show-modal]').click();
    const [accessKeyId, secretKey] = readFileSync(join(stateDir, 'r2'), 'utf8').trim().split('\n');
    await page.locator('#bucket-endpoint').fill('https://journey.backups.terrascale.tech');
    await page.locator('#bucket-region').fill('auto');
    await page.locator('#bucket-name').fill('journey-backups');
    await page.locator('#bucket-key').fill(accessKeyId);
    await page.locator('#bucket-secret').fill(secretKey);
    await page.locator('#bucket-dialog').getByRole('button', { name: 'Save bucket', exact: true }).click();
    await expect(page.locator('#bucket-dialog')).not.toBeVisible();
    await expect(page.locator('#backup-page')).toContainText('journey-backups');
    await page.locator('button[commandfor=bucket-dialog][command=show-modal]').click();
    await page.locator('#bucket-dialog').getByRole('button', { name: 'Cancel', exact: true }).click();
    await audit.check('backup destination saved and page remains interactive');
    await page.locator('button[commandfor=remove-backup-bucket][command=show-modal]').first().click();
    await page.locator('#remove-backup-bucket').first().getByRole('button', { name: 'Remove bucket', exact: true }).click();
    await expect(page.locator('button[commandfor=remove-backup-bucket][command=show-modal]')).toHaveCount(0);
    await audit.finish();
    await saveCookies(context);
    await context.close();
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('R2 add a region and read a row committed before the addition', async ({ cells }) => {
  const created = await create(cells, ['gru']);
  try {
    await seed(cells, created.id);
    const added = await place(cells, created.id, ['fra', 'gru']);
    expect(added.placementVersion, 'adding a region raises the placement version').toBeGreaterThan(created.placementVersion);
    await replicated(cells, fra, created.id, 3);
    await healthy(cells, gru, created.id, ['fra', 'gru']);
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('R4 replace the only region and read every committed row in the new one', async ({ cells }) => {
  const created = await create(cells, ['gru']);
  try {
    await seed(cells, created.id);
    const replaced = await place(cells, created.id, ['fra']);
    expect(replaced.placementVersion, 'replacing the region raises the placement version').toBeGreaterThan(created.placementVersion);
    expect(await converged(cells, fra, created.id, 'SELECT value FROM placement_rows ORDER BY value', rows => rows.length === 3)).toEqual([[1], [2], [3]]);
    await healthy(cells, fra, created.id, ['fra']);
    const written = await answer(cells, fra, created.id, ['INSERT INTO placement_rows VALUES (4)']);
    expect((await answer(cells, fra, created.id, ['SELECT value FROM placement_rows ORDER BY value'], { bookmark: written.bookmark })).results[0].rows)
      .toEqual([[1], [2], [3], [4]]);
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('R7 unchanged placement keeps its version and an unknown region is refused', async ({ cells }) => {
  const created = await create(cells, ['gru', 'fra']);
  try {
    const before = await json<Record<string, unknown>>(await cells.get(`${gru}${database(created.id)}`));
    expect(before.placementVersion, 'placementVersion is exposed').toEqual(expect.any(Number));
    const repeated = await json<Record<string, unknown>>(await cells.patch(`${gru}${database(created.id)}`, { data: { regions: ['fra', 'gru'] } }));
    expect(repeated.placementVersion, 'an identical region set keeps placementVersion').toBe(before.placementVersion);
    expect(repeated.regions).toEqual(['fra', 'gru']);
    console.log(`R7 placementVersion unchanged: ${before.placementVersion}`);
    const unknown = await refused(await cells.patch(`${gru}${database(created.id)}`, { data: { regions: ['gru', 'fra', 'atlantis'] } }), 400, 'unknown_region');
    expect(unknown.error.message).toMatch(/^unknown region atlantis; the regions are .*\bfra\b.*\bgru\b/);
    const after = await json<Record<string, unknown>>(await cells.get(`${gru}${database(created.id)}`));
    expect(after.regions, 'a refused placement changes nothing').toEqual(['fra', 'gru']);
    expect(after.placementVersion).toBe(repeated.placementVersion);
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('R8 a TTL branch keeps its identity through catalog sync and a rename in every region', async ({ browser, playwright, request }, info) => {
  const context = await auditedContext(browser, {});
  try {
    const page = await context.newPage();
    const audit = new Audit(page, info);
    const email = await newAddress(name('identity'));
    const password = randomBytes(24).toString('base64url');
    saveSecret('identity-password', password);
    await signUp(page, audit, email, password, 'database identity owner');
    await verify(page, email);
    await onboard(page, audit, 'Database identity owner', name('identity-project'), 'database identity owner');
    const projectId = new URL(page.url()).searchParams.get('projectId') ?? '';
    expect(projectId).not.toBe('');
    await page.goto(`${dash}/organization?tab=billing`);
    await page.locator('article.ts-plan', { has: page.locator('#plan-pro') }).getByRole('button', { name: 'Upgrade to Pro', exact: true }).click();
    await page.waitForURL(url => url.origin === 'https://checkout.stripe.com' && url.pathname.startsWith('/c/pay/cs_test_'));
    await page.getByRole('button', { name: 'Subscribe', exact: true }).click();
    await page.waitForURL(url => url.origin === dash && url.pathname === '/organization' && url.searchParams.get('payment') === 'success');
    await expect(page.locator('#billing-summary .ts-billing-plan-name')).toHaveText('Pro');
    await audit.check('placement branch owner upgraded');
    const key = await createApiKey(page, audit, name('identity-key'), 'database identity');
    const cells = await authenticatedCells(playwright.request, request, key, projectId);
    try {
      const source = await create(cells, ['gru'], projectId);
      let branch: Database | undefined;
      try {
        await seed(cells, source.id, projectId);
        branch = await json<Database>(await cells.post(`${gru}${database(source.id, projectId)}/branches`, {
          data: { name: name('identity'), ttlSeconds: 3600 },
        }), 201);
        const id = branch.id;
        const original = await json<Record<string, unknown>>(await cells.get(`${gru}${database(id, projectId)}`));
        expect(original.branchedFrom).toBe(source.id);
        expect(original.createdAt).toEqual(expect.any(String));
        expect(original.expiresAt).toEqual(expect.any(String));
        await place(cells, id, ['gru', 'fra'], projectId);
        await replicated(cells, fra, id, 3, projectId);

        const renamed = await json<Record<string, unknown>>(await cells.patch(`${gru}${database(id, projectId)}`, { data: { name: name('renamed') } }));
        const extended = await json<Record<string, unknown>>(await cells.put(`${gru}${database(id, projectId)}/ttl`, { data: { ttlSeconds: 7200 } }));
        expect(extended.name).toBe(renamed.name);
        expect(extended.expiresAt).not.toBe(original.expiresAt);
        const facts = ({ region, homeRegion, replicaRegions, placementVersion, ttlSeconds, ...identity }: Record<string, unknown>) => identity;
        expect(extended).toMatchObject({ id, branchedFrom: source.id, createdAt: original.createdAt, previousName: original.name, regions: ['fra', 'gru'] });
        await expect.poll(async () => facts(await json<Record<string, unknown>>(await cells.get(`${fra}${database(id, projectId)}`))),
          { message: 'fra receives the name and expiry changed through gru', timeout: 45_000, intervals: [250, 500, 1000] })
          .toEqual(facts(extended));

        const cleared = await json<Record<string, unknown>>(await cells.delete(`${fra}${database(id, projectId)}/ttl`));
        expect(cleared.expiresAt).toBeNull();
        await expect.poll(async () => facts(await json<Record<string, unknown>>(await cells.get(`${gru}${database(id, projectId)}`))),
          { message: 'gru receives the TTL cleared through fra', timeout: 45_000, intervals: [250, 500, 1000] })
          .toEqual(facts(cleared));

        const changedName = name('renamed-in-fra');
        const renamedInFra = await json<Record<string, unknown>>(await cells.patch(`${fra}${database(id, projectId)}`, { data: { name: changedName } }));
        expect(facts(renamedInFra)).toEqual({ ...facts(cleared), name: changedName, previousName: cleared.name });
        await expect.poll(async () => facts(await json<Record<string, unknown>>(await cells.get(`${gru}${database(id, projectId)}`))),
          { message: 'gru receives the name changed through fra', timeout: 45_000, intervals: [250, 500, 1000] })
          .toEqual(facts(renamedInFra));
        console.log('R8 both regions agree on the branch identity after TTL and name changes made through either region');
      } finally {
        if (branch) await removeDatabase(cells, branch.id, projectId);
        await removeDatabase(cells, source.id, projectId);
      }
    } finally {
      await cells.dispose();
    }

    await page.goto(`${dash}/profile?tab=danger`);
    await page.locator('main').getByRole('button', { name: 'Delete account', exact: true }).click();
    const dialog = page.locator('#delete-account');
    await dialog.locator('#delete-account-email').fill(email);
    await dialog.getByRole('button', { name: 'Delete account', exact: true }).click();
    await confirmWithPassword(page, audit, password, 'database identity owner deletion');
    await page.waitForURL(url => url.origin === dash && url.pathname === '/account-deleted');
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Account deleted');
    await audit.check('database identity owner deleted');
    await audit.finish();
  } finally {
    await saveCookies(context);
    await context.close();
  }
});

test('R20 the create-database picker shows a measured ping for every region', async ({ browser }, info) => {
  const context = await auditedContext(browser, {});
  try {
    const page = await context.newPage();
    const audit = new Audit(page, info);
    const email = await newAddress(name('ping'));
    const password = randomBytes(24).toString('base64url');
    saveSecret('ping-owner-password', password);
    await signUp(page, audit, email, password, 'region ping owner');
    await verify(page, email);
    await onboard(page, audit, 'Region ping owner', name('ping-project'), 'region ping owner');
    await page.goto(`${dash}/databases`);
    await page.getByRole('button', { name: 'New database', exact: true }).first().click();
    await expect(page.locator('#create-database[open]')).toBeVisible();
    const callouts = page.locator('#create-database-regions .ts-region-callout[data-region]');
    await expect(callouts).not.toHaveCount(0);
    const settled = callouts.locator('[data-region-ping]');
    await expect(settled).toHaveCount(await callouts.count());
    for (const badge of await settled.all()) {
      await expect(badge).toHaveText(/\d+ ms|Unreachable/, { timeout: 15_000 });
    }
    const measured = await settled.filter({ hasText: /\d+ ms/ }).count();
    expect(measured, 'at least one reachable region shows a number in ms').toBeGreaterThan(0);
    await audit.check('database create picker ping badges');
    await page.locator('#create-database .ts-dialog-head button[type="button"]').click();
    await page.goto(`${dash}/profile?tab=danger`);
    await page.locator('main').getByRole('button', { name: 'Delete account', exact: true }).click();
    const dialog = page.locator('#delete-account');
    await dialog.locator('#delete-account-email').fill(email);
    await dialog.getByRole('button', { name: 'Delete account', exact: true }).click();
    await confirmWithPassword(page, audit, password, 'region ping owner deletion');
    await page.waitForURL(url => url.origin === dash && url.pathname === '/account-deleted');
  } finally {
    await context.close();
  }
});

test('R22 discovery lists gru and fra as equal regions that both accept writes', async ({ cells }) => {
  for (const origin of [gru, fra]) {
    const catalog = await json<Catalog>(await cells.get(`${origin}/v1/regions`));
    expect(catalog.region).toBe(regionOf(origin));
    const listed = catalog.regions.filter(region => region.id === 'gru' || region.id === 'fra');
    expect(listed.map(region => region.id)).toEqual(['fra', 'gru']);
    for (const region of listed) {
      expect(region.acceptingWrites, `${region.id} accepts writes`).toBe(true);
      expect(region.local, `${region.id} is local only to its own listener`).toBe(region.id === regionOf(origin));
      expect(region, `${region.id} has no role`).not.toHaveProperty('role');
    }
  }
});

test('R23 concurrent writes in gru and fra converge on identical rows in both regions', async ({ cells }) => {
  const created = await create(cells, ['gru', 'fra']);
  try {
    const seeded = await answer(cells, gru, created.id, ['CREATE TABLE items (id INTEGER PRIMARY KEY, value TEXT NOT NULL)', "INSERT INTO items VALUES (0, 'seed')"]);
    expect((await answer(cells, fra, created.id, ['SELECT id, value FROM items ORDER BY id'], { bookmark: seeded.bookmark })).results[0].rows).toEqual([[0, 'seed']]);
    const [atGru, atFra] = await Promise.all([
      answer(cells, gru, created.id, ["INSERT INTO items VALUES (1, 'gru')", "UPDATE items SET value = 'gru' WHERE id = 0"]),
      answer(cells, fra, created.id, ["INSERT INTO items VALUES (2, 'fra')", "UPDATE items SET value = 'fra' WHERE id = 0"]),
    ]);
    expect([atGru.consistency, atFra.consistency], 'both regions committed locally').toEqual(['fast', 'fast']);
    await expect.poll(async () => {
      const [gruRows, fraRows] = await Promise.all([gru, fra].map(async origin =>
        (await answer(cells, origin, created.id, ['SELECT id, value FROM items ORDER BY id'])).results[0].rows));
      return JSON.stringify(gruRows) === JSON.stringify(fraRows) ? gruRows : { gru: gruRows, fra: fraRows };
    }, { message: 'gru and fra converge on the same rows', timeout: 45_000, intervals: [250, 500, 1000] })
      .toEqual([[0, expect.stringMatching(/^(gru|fra)$/)], [1, 'gru'], [2, 'fra']]);
    const merged = (await answer(cells, gru, created.id, ['SELECT value FROM items WHERE id = 0'], { bookmark: atFra.bookmark })).results[0].rows;
    console.log(`R23 concurrent same-row updates resolved to ${JSON.stringify(merged)} in both regions`);
    await healthy(cells, fra, created.id, ['fra', 'gru']);
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('R24 a bookmark reads a write made in the other region at once, in both directions', async ({ cells }) => {
  const created = await create(cells, ['gru', 'fra']);
  try {
    const schema = await answer(cells, gru, created.id, ['CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL)']);
    const inGru = await answer(cells, gru, created.id, ["INSERT INTO notes VALUES (1, 'written in gru')"], { bookmark: schema.bookmark });
    expect(inGru.bookmark).not.toBe('');
    const readInFra = await answer(cells, fra, created.id, ['SELECT id, body FROM notes ORDER BY id'], { bookmark: inGru.bookmark });
    expect(readInFra.results[0].rows, 'fra waits for the gru bookmark before answering').toEqual([[1, 'written in gru']]);
    const inFra = await answer(cells, fra, created.id, ["INSERT INTO notes VALUES (2, 'written in fra')"], { bookmark: readInFra.bookmark });
    const readInGru = await answer(cells, gru, created.id, ['SELECT id, body FROM notes ORDER BY id'], { bookmark: inFra.bookmark });
    expect(readInGru.results[0].rows, 'gru waits for the fra bookmark before answering').toEqual([[1, 'written in gru'], [2, 'written in fra']]);
    const malformed = await run(cells, fra, created.id, ['SELECT 1'], { bookmark: 'not a bookmark' });
    const invalid = await refused(malformed, 400, 'invalid_bookmark');
    expect(invalid.error.sqlState).toBe('22023');
    await refused(await cells.post(`${gru}${database(created.id)}/sql`, {
      data: { statements: [{ sql: 'SELECT 1' }] }, headers: { [consistencyHeader]: 'eventual' },
    }), 400, 'invalid_consistency');
    const unset = await run(cells, gru, created.id, ['SELECT 1']);
    expect(unset.status()).toBe(200);
    expect(unset.headers()[bookmarkHeader], 'every answer carries a bookmark header').toEqual(expect.any(String));
    console.log('R24 a gru write was visible in fra and a fra write in gru on the first read that carried its bookmark');
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('R25 conflicting strong transactions in gru and fra: one commits, the other is refused', async ({ cells, request, page }) => {
  const created = await create(cells, ['gru', 'fra']);
  try {
    const seeded = await answer(cells, gru, created.id, ['CREATE TABLE ledger (id INTEGER PRIMARY KEY, value INTEGER NOT NULL)', 'INSERT INTO ledger VALUES (1, 0)']);
    expect((await answer(cells, fra, created.id, ['SELECT value FROM ledger WHERE id = 1'], { bookmark: seeded.bookmark })).results[0].rows).toEqual([[0]]);
    const frames = await strongRace(page, created.id, await sqlToken(request));
    const outcome = (region: 'gru' | 'fra') => {
      const error = frames[region].find(frame => frame.type === 'error');
      return error ? { committed: false, code: error.error?.code, sqlState: error.error?.sqlState } : { committed: frames[region].at(-1)?.type === 'result' };
    };
    const results = { gru: outcome('gru'), fra: outcome('fra') };
    console.log('R25 strong race: ' + JSON.stringify(results));
    expect([results.gru.committed, results.fra.committed].filter(Boolean), 'exactly one strong transaction commits').toHaveLength(1);
    const loser = results.gru.committed ? results.fra : results.gru;
    expect(loser).toEqual({ committed: false, code: 'serialization_conflict', sqlState: '40001' });
    const winner = results.gru.committed ? 1 : 10;
    for (const origin of [gru, fra]) {
      expect(await converged(cells, origin, created.id, 'SELECT value FROM ledger WHERE id = 1', rows => rows[0]?.[0] === winner)).toEqual([[winner]]);
    }
    const strong = await answer(cells, fra, created.id, ['UPDATE ledger SET value = value + 100 WHERE id = 1'], { consistency: 'strong' });
    expect(strong.consistency).toBe('strong');
    expect((await answer(cells, gru, created.id, ['SELECT value FROM ledger WHERE id = 1'], { bookmark: strong.bookmark, consistency: 'strong' })).results[0].rows)
      .toEqual([[winner + 100]]);
  } finally {
    await removeDatabase(cells, created.id);
  }
});
