import { randomBytes } from 'node:crypto';
import { setTimeout as delay } from 'node:timers/promises';
import { expect, type APIResponse } from '@playwright/test';
import { test, authenticatedCells, home, replica, project, database, name, json, sql, replicated, removeDatabase, type Cells } from './support';
import { dash, production } from '../support/stack';
import { Audit, auditedContext } from '../support/audit';
import { saveCookies, saveSecret } from '../support/state';
import { confirmWithPassword, onboard, signUp, verify } from '../support/hosted';
import { newAddress } from '../support/inbox';
import { createApiKey } from '../support/paid';

test.skip(production || process.env.E2E_REGIONS !== '2', 'placement requires the local stack started with E2E_REGIONS=2');
test.describe.configure({ mode: 'default', timeout: 180_000 });

type Database = { id: string; regions: string[] };
type Observation = { region: string; role: string | null; revision: string | null; lastPulledAt: string | null; lagMs: number | null };
type Progress = { regions: Observation[] };

async function answer(response: APIResponse, code: number, documented: string): Promise<void> {
  expect(response.status(), response.status() === code ? documented : `${documented}: ${await response.text()}`).toBe(code);
  if (code >= 400) expect((await response.json()).error.code, documented).toBe(documented);
}

async function create(cells: Cells, regions: string[], projectId?: string): Promise<Database> {
  const response = await cells.post(`${home}${project(projectId)}/databases`, { data: { name: name('regions'), regions } });
  await answer(response, 201, '201 database created');
  return response.json();
}

async function progress(cells: Cells, id: string, region = home, projectId?: string): Promise<Progress> {
  let response = await cells.get(`${region}${database(id, projectId)}/regions`);
  if (response.status() === 421) {
    await answer(response, 421, 'wrong_region');
    response = await cells.get(`https://${response.headers()['x-terrabase-database-home']}.api.terrascale.tech${database(id, projectId)}/regions`);
  }
  await answer(response, 200, '200 database regions');
  return response.json();
}

async function observed(cells: Cells, id: string, projectId?: string): Promise<void> {
  await expect.poll(async () => (await progress(cells, id, home, projectId)).regions.find(row => row.region === 'fra'),
    { message: 'fra has a completed pull observation', timeout: 45_000, intervals: [250, 500, 1000] })
    .toMatchObject({ revision: expect.any(String), lastPulledAt: expect.any(String), lagMs: expect.any(Number) });
}

async function seed(cells: Cells, id: string, projectId?: string): Promise<void> {
  await sql(cells, home, id, 'CREATE TABLE placement_rows (value INTEGER PRIMARY KEY)', projectId);
  await sql(cells, home, id, 'INSERT INTO placement_rows VALUES (1), (2), (3)', projectId);
}

async function patch(cells: Cells, id: string, regions: string[], projectId?: string): Promise<APIResponse> {
  let response = await cells.patch(`${home}${database(id, projectId)}`, { data: { regions } });
  if (response.status() === 421) {
    await answer(response, 421, 'wrong_region');
    response = await cells.patch(`https://${response.headers()['x-terrabase-database-home']}.api.terrascale.tech${database(id, projectId)}`, { data: { regions } });
  }
  return response;
}

async function move(cells: Cells, id: string, label: string, timeout: number, statuses: number[] = [], projectId?: string): Promise<void> {
  try {
    await expect.poll(async () => {
      const response = await patch(cells, id, ['fra'], projectId);
      statuses.push(response.status());
      if (response.status() === 200) {
        await answer(response, 200, '200 database updated');
        expect((await response.json()).regions).toEqual(['fra']);
        return true;
      }
      await answer(response, 503, 'replica_not_ready');
      return false;
    }, { message: `${label} reaches 200`, timeout, intervals: [1000] }).toBe(true);
  } finally {
    console.log(`${label} statuses: ${JSON.stringify(statuses)}`);
  }
  expect((await progress(cells, id, replica, projectId)).regions.map(row => row.region)).toEqual(['fra']);
}

test('R1 create two regions, read across listeners and observe a pull', async ({ cells }) => {
  const created = await create(cells, ['gru', 'fra']);
  try {
    expect(created.regions).toEqual(['fra', 'gru']);
    await seed(cells, created.id);
    await replicated(cells, replica, created.id, 3);
    await observed(cells, created.id);
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('R2 add a region and read a row committed before the addition', async ({ cells }) => {
  const created = await create(cells, ['gru']);
  try {
    await seed(cells, created.id);
    const added = await patch(cells, created.id, ['fra', 'gru']);
    await answer(added, 200, '200 database updated with writer retained');
    expect((await added.json()).regions).toEqual(['fra', 'gru']);
    await observed(cells, created.id);
    await replicated(cells, replica, created.id, 3);
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('R3 replace the writer when both regions are members', async ({ cells }) => {
  const created = await create(cells, ['gru', 'fra']);
  try {
    await seed(cells, created.id);
    await observed(cells, created.id);
    await move(cells, created.id, 'R3', 60_000);
    await sql(cells, replica, created.id, 'INSERT INTO placement_rows VALUES (4)');
    await expect.poll(async () => {
      const response = await cells.post(`${home}${database(created.id)}/sql/execute`, { data: { statements: [{ sql: 'SELECT value FROM placement_rows' }] } });
      if (response.status() === 421) await answer(response, 421, 'wrong_region');
      else await answer(response, 200, '200 during catalog propagation');
      return response.status();
    }, { message: '421 wrong_region from a non-member listener', timeout: 45_000, intervals: [250, 500, 1000] }).toBe(421);
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('R4 replace the only region, retain additions after the first refusal', async ({ cells }) => {
  const created = await create(cells, ['gru']);
  const statuses: number[] = [];
  try {
    await seed(cells, created.id);
    const first = await patch(cells, created.id, ['fra']);
    statuses.push(first.status());
    await answer(first, 503, 'replica_not_ready');
    expect((await progress(cells, created.id)).regions.map(row => row.region)).toEqual(['fra', 'gru']);
    await move(cells, created.id, 'R4', 90_000, statuses);
    expect((await sql(cells, replica, created.id, 'SELECT value FROM placement_rows ORDER BY value')).results[0].rows).toEqual([[1], [2], [3]]);
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('R5 replace the writer under twenty acknowledged commits per second', async ({ cells }) => {
  test.setTimeout(180_000);
  const created = await create(cells, ['gru', 'fra']);
  const acknowledged: number[] = [];
  const lags: number[] = [];
  const statuses: number[] = [];
  const backgroundErrors: unknown[] = [];
  let stop = false;
  let writer: Promise<void> | undefined;
  let sampling: Promise<void> | undefined;
  try {
    await sql(cells, home, created.id, 'CREATE TABLE placement_rows (value INTEGER PRIMARY KEY)');
    await observed(cells, created.id);
    const deadline = Date.now() + 120_000;
    writer = (async () => {
      for (let id = 1; !stop && Date.now() < deadline;) {
        const next = Date.now() + 50;
        const response = await cells.post(`${home}${database(created.id)}/sql/execute`, { data: { statements: [{ sql: `INSERT INTO placement_rows VALUES (${id})` }] } });
        if (statuses.length > 0 || response.status() !== 200) statuses.push(response.status());
        if (response.status() === 200) {
          acknowledged.push(id++);
        } else if (response.status() === 409) {
          await answer(response, 409, 'serialization_conflict');
        } else {
          await answer(response, 421, 'wrong_region');
          return;
        }
        await delay(Math.max(0, next - Date.now()));
      }
    })().catch(error => { backgroundErrors.push(error); });
    await expect.poll(() => acknowledged.length, { message: 'writer commits before the move', timeout: 10_000, intervals: [50] }).toBeGreaterThanOrEqual(20);
    const sample = async () => {
      const row = (await progress(cells, created.id)).regions.find(row => row.region === 'fra');
      if (row?.lagMs !== null && row?.lagMs !== undefined) lags.push(row.lagMs);
    };
    await sample();
    sampling = (async () => {
      while (!stop && Date.now() < deadline) {
        await sample();
        await delay(1000);
      }
    })().catch(error => { backgroundErrors.push(error); });
    await move(cells, created.id, 'R5', Math.max(1, deadline - Date.now()));
    await writer;
    stop = true;
    await sampling;
    const result = await sql(cells, replica, created.id, 'SELECT value FROM placement_rows ORDER BY value');
    const found = result.results[0].rows.map(row => row[0]);
    console.log(`R5 writer statuses: ${JSON.stringify(statuses)}; acknowledged: ${acknowledged.length}; found: ${found.length}`);
    if (JSON.stringify(found) !== JSON.stringify(acknowledged)) console.log(`R5 database: ${created.id}; acknowledged ids: ${JSON.stringify(acknowledged)}; found ids: ${JSON.stringify(found)}`);
    expect(found).toEqual(acknowledged);
    expect(new Set(found).size).toBe(found.length);
    expect(lags.length, 'fra lag samples').toBeGreaterThan(0);
    if (backgroundErrors.length > 0) throw new AggregateError(backgroundErrors, 'R5 writer or lag sampling failed');
    expect(statuses.at(-1), '421 wrong_region terminates writes through the former writer').toBe(421);
  } finally {
    stop = true;
    await Promise.all([writer, sampling]);
    lags.sort((a, b) => a - b);
    console.log(`R5 lagMs min/median/max: ${lags[0]}/${lags[Math.floor(lags.length / 2)]}/${lags.at(-1)}; samples: ${lags.length}`);
    await removeDatabase(cells, created.id);
  }
});

test('R6 repeat and conflicting placements during a move', async ({ cells }) => {
  test.setTimeout(20 * 60_000 + 30_000);
  let caught = false;
  for (let attempt = 1; attempt <= 20 && !caught; attempt++) {
    const created = await create(cells, ['gru', 'fra']);
    try {
      await seed(cells, created.id);
      await observed(cells, created.id);
      const moving = patch(cells, created.id, ['fra']);
      const concurrent = [
        await patch(cells, created.id, ['fra']),
        await patch(cells, created.id, ['gru', 'fra']),
      ];
      const initial = await moving;
      for (const response of [initial, ...concurrent]) {
        expect([200, 503, 409], '200 updated, 503 replica_not_ready or 409 move_in_progress').toContain(response.status());
        if (response.status() === 503) await answer(response, 503, 'replica_not_ready');
        if (response.status() === 409) await answer(response, 409, 'move_in_progress');
      }
      caught = concurrent[0].status() === 503 && concurrent[1].status() === 409;
      console.log(`R6 attempt ${attempt}: ${[initial, ...concurrent].map(response => response.status()).join(',')}`);
      await move(cells, created.id, 'R6 cleanup move', 60_000);
    } finally {
      await removeDatabase(cells, created.id);
    }
  }
  console.log(`R6: ${caught ? 'PASS' : 'not observed after 20 attempts'}`);
});

test('R7 unchanged placement and the documented nine-member maximum', async ({ cells }) => {
  const created = await create(cells, ['gru', 'fra']);
  try {
    const before = await json<Record<string, unknown>>(await cells.get(`${home}${database(created.id)}`));
    const repeated = await patch(cells, created.id, ['fra', 'gru']);
    await answer(repeated, 200, '200 unchanged database');
    expect(await repeated.json()).toEqual(before);
    expect(before.placementVersion, 'placementVersion is exposed').toEqual(expect.any(Number));
    const after = await json<Record<string, unknown>>(await cells.get(`${home}${database(created.id)}`));
    expect(after.placementVersion, 'unchanged request retains placementVersion').toBe(before.placementVersion);
    console.log(`R7 placementVersion unchanged: ${before.placementVersion}`);
    const over = await patch(cells, created.id, ['gru', 'fra', 'sjc', 'iad', 'lhr', 'cdg', 'sin', 'syd', 'nrt', 'bom']);
    await answer(over, 400, 'invalid_request');
    expect((await over.json()).error.message).toBe('regions can place a database in at most 9 regions, including the writer and any intermediate placement');
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('R8 a TTL branch keeps its identity through catalog sync, a move and a rename', async ({ browser, playwright, request }, info) => {
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
        branch = await json<Database>(await cells.post(`${home}${database(source.id, projectId)}/branches`, {
          data: { name: name('identity'), ttlSeconds: 3600 },
        }), 201);
        const id = branch.id;
        const original = await json<Record<string, unknown>>(await cells.get(`${home}${database(id, projectId)}`));
        expect(original.branchedFrom).toBe(source.id);
        expect(original.createdAt).toEqual(expect.any(String));
        expect(original.expiresAt).toEqual(expect.any(String));
        await answer(await patch(cells, id, ['gru', 'fra'], projectId), 200, '200 database updated with writer retained');
        await observed(cells, id, projectId);
        await replicated(cells, replica, id, 3, projectId);

        const renamed = await json<Record<string, unknown>>(await cells.patch(`${home}${database(id, projectId)}`, { data: { name: name('before-move') } }));
        const before = await json<Record<string, unknown>>(await cells.put(`${home}${database(id, projectId)}/ttl`, { data: { ttlSeconds: 7200 } }));
        expect(before.name).toBe(renamed.name);
        expect(before.expiresAt).not.toBe(original.expiresAt);
        expect(before.placementVersion).toBe(renamed.placementVersion);
        const facts = ({ region, homeRegion, replicaRegions, placementVersion, allRegions, regions, ...identity }: Record<string, unknown>) => identity;
        expect(facts(before)).toEqual({ ...facts(original), name: before.name, expiresAt: before.expiresAt });
        await expect.poll(async () => facts(await json<Record<string, unknown>>(await cells.get(`${replica}${database(id, projectId)}`))),
          { message: 'the existing replica receives name and expiry changes without a placement change', timeout: 45_000, intervals: [250, 500, 1000] })
          .toEqual(facts(before));

        const atPromotion = await json<Record<string, unknown>>(await cells.put(`${home}${database(id, projectId)}/ttl`, { data: { ttlSeconds: 10800 } }));
        expect(atPromotion.expiresAt).not.toBe(before.expiresAt);
        await move(cells, id, 'R8', 60_000, [], projectId);
        const moved = await json<Record<string, unknown>>(await cells.get(`${replica}${database(id, projectId)}`));
        expect(facts(moved)).toEqual(facts(atPromotion));
        const changedName = name('after-move');
        const renamedAfterMove = await json<Record<string, unknown>>(await cells.patch(`${replica}${database(id, projectId)}`, { data: { name: changedName } }));
        expect(renamedAfterMove).toEqual({ ...moved, name: changedName });
        expect(await json(await cells.get(`${replica}${database(id, projectId)}`))).toEqual(renamedAfterMove);
        console.log('R8 createdAt, branchedFrom, expiresAt and every non-placement field retained; rename changes only name');
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

const multiple = test.extend<{ owner: { cells: Cells; projectId: string } }>({
  owner: async ({ browser, playwright, request }, use, info) => {
    const context = await auditedContext(browser, {});
    try {
      const page = await context.newPage();
      const audit = new Audit(page, info);
      const email = await newAddress(name('catalog'));
      const password = randomBytes(24).toString('base64url');
      saveSecret('catalog-password', password);
      await signUp(page, audit, email, password, 'catalog owner');
      await verify(page, email);
      await onboard(page, audit, 'Catalog owner', name('catalog-project'), 'catalog owner');
      const projectId = new URL(page.url()).searchParams.get('projectId') ?? '';
      expect(projectId).not.toBe('');
      await page.goto(`${dash}/organization?tab=billing`);
      await page.locator('article.ts-plan', { has: page.locator('#plan-pro') }).getByRole('button', { name: 'Upgrade to Pro', exact: true }).click();
      await page.waitForURL(url => url.origin === 'https://checkout.stripe.com' && url.pathname.startsWith('/c/pay/cs_test_'));
      await page.getByRole('button', { name: 'Subscribe', exact: true }).click();
      await page.waitForURL(url => url.origin === dash && url.pathname === '/organization' && url.searchParams.get('payment') === 'success');
      await expect(page.locator('#billing-summary .ts-billing-plan-name')).toHaveText('Pro');
      await audit.check('catalog owner upgraded');
      const key = await createApiKey(page, audit, name('catalog-key'), 'catalog owner');
      const cells = await authenticatedCells(playwright.request, request, key, projectId);
      try {
        await use({ cells, projectId });
      } finally {
        await cells.dispose();
      }
      await page.goto(`${dash}/profile?tab=danger`);
      await page.locator('main').getByRole('button', { name: 'Delete account', exact: true }).click();
      const dialog = page.locator('#delete-account');
      await dialog.locator('#delete-account-email').fill(email);
      await dialog.getByRole('button', { name: 'Delete account', exact: true }).click();
      await confirmWithPassword(page, audit, password, 'catalog owner deletion');
      await page.waitForURL(url => url.origin === dash && url.pathname === '/account-deleted');
      await audit.check('catalog owner deleted');
      await audit.finish();
    } finally {
      await saveCookies(context);
      await context.close();
    }
  },
});

async function named(cells: Cells, region: string, label: string, regions: string[], projectId: string): Promise<Database> {
  return json<Database>(await cells.post(`${region}${project(projectId)}/databases`, { data: { name: label, regions } }), 201);
}

async function rename(cells: Cells, region: string, id: string, label: string, projectId: string): Promise<void> {
  await answer(await cells.patch(`${region}${database(id, projectId)}`, { data: { name: label } }), 200, '200 database renamed');
}

async function recordedName(cells: Cells, id: string, projectId: string): Promise<string> {
  return (await json<{ name: string }>(await cells.get(`${replica}${database(id, projectId)}`))).name;
}

async function convergedNames(cells: Cells, ids: string[], real: string[], expected: string[], projectId: string): Promise<void> {
  let samples = 0;
  await expect.poll(async () => {
    const names = await Promise.all(ids.map(id => recordedName(cells, id, projectId)));
    for (const value of names) expect(real, 'management GET never exposes a placeholder').toContain(value);
    samples++;
    return names;
  }, { message: 'fra applies the chain within two 30-second sweeps', timeout: 65_000, intervals: [200] }).toEqual(expected);
  console.log('management GET every 200 ms: ' + samples + ' samples per database, only real names');
}

multiple('R9 a taken follower name does not stop later catalog entries or pulls', async ({ owner: { cells, projectId } }) => {
  const original = name('original');
  const occupied = name('occupied');
  const x = await named(cells, home, original, ['gru', 'fra'], projectId);
  const y = await named(cells, replica, occupied, ['fra'], projectId);
  let later: Database | undefined;
  try {
    await seed(cells, x.id, projectId);
    await observed(cells, x.id, projectId);
    await rename(cells, home, x.id, occupied, projectId);
    later = await create(cells, ['gru'], projectId);
    await seed(cells, later.id, projectId);
    await answer(await patch(cells, later.id, ['gru', 'fra'], projectId), 200, '200 later database starts following');
    await observed(cells, later.id, projectId);
    await replicated(cells, replica, later.id, 3, projectId);
    expect(await recordedName(cells, x.id, projectId)).toBe(original);
    expect(await recordedName(cells, y.id, projectId)).toBe(occupied);
    console.log('R9 fra kept the conflicting name and completed a pull for a later gru database');
  } finally {
    if (later) await removeDatabase(cells, later.id, projectId);
    await removeDatabase(cells, x.id, projectId);
    await removeDatabase(cells, y.id, projectId);
  }
});

multiple('R10 a home name swap converges on the follower within two sweeps', async ({ owner: { cells, projectId } }) => {
  const a = name('swap-a');
  const b = name('swap-b');
  const x = await named(cells, home, a, ['gru', 'fra'], projectId);
  const y = await named(cells, home, b, ['gru', 'fra'], projectId);
  try {
    await seed(cells, x.id, projectId);
    await seed(cells, y.id, projectId);
    await observed(cells, x.id, projectId);
    await observed(cells, y.id, projectId);
    expect(await recordedName(cells, x.id, projectId)).toBe(a);
    expect(await recordedName(cells, y.id, projectId)).toBe(b);
    const started = performance.now();
    await rename(cells, home, x.id, name('swap-tmp'), projectId);
    await rename(cells, home, y.id, a, projectId);
    await rename(cells, home, x.id, b, projectId);
    expect(performance.now() - started, 'all three renames complete in less than one sweep').toBeLessThan(30_000);
    expect(await Promise.all([x.id, y.id].map(id => recordedName(cells, id, projectId))),
      'fra still has the original names after every home rename completed, so it missed the intermediate name').toEqual([a, b]);
    await convergedNames(cells, [x.id, y.id], [a, b], [b, a], projectId);
    console.log('R10 fra converged to both swapped names within two catalog sweeps');
  } finally {
    await removeDatabase(cells, x.id, projectId);
    await removeDatabase(cells, y.id, projectId);
  }
});

multiple('R11 a conflicting promotion is refused before takeover and succeeds after a rename', async ({ owner: { cells, projectId } }) => {
  const occupied = name('move-name');
  const original = name('move-original');
  const x = await named(cells, home, original, ['gru', 'fra'], projectId);
  const y = await named(cells, replica, occupied, ['fra'], projectId);
  try {
    await seed(cells, x.id, projectId);
    await observed(cells, x.id, projectId);
    await rename(cells, home, x.id, occupied, projectId);
    const before = await json<Record<string, unknown>>(await cells.get(`${home}${database(x.id, projectId)}`));
    await answer(await patch(cells, x.id, ['fra'], projectId), 503, 'replica_not_ready');
    const after = await json<Record<string, unknown>>(await cells.get(`${home}${database(x.id, projectId)}`));
    expect(after).toEqual(before);
    expect(await recordedName(cells, x.id, projectId)).toBe(original);
    expect((await progress(cells, x.id, home, projectId)).regions.filter(row => row.role === 'writer').map(row => row.region)).toEqual(['gru']);
    await sql(cells, home, x.id, 'INSERT INTO placement_rows VALUES (4)', projectId);
    await rename(cells, replica, y.id, name('move-freed'), projectId);
    await move(cells, x.id, 'R11', 60_000, [], projectId);
    expect((await sql(cells, replica, x.id, 'SELECT value FROM placement_rows ORDER BY value', projectId)).results[0].rows).toEqual([[1], [2], [3], [4]]);
    expect(await recordedName(cells, x.id, projectId)).toBe(occupied);
    console.log('R11 replica_not_ready left gru writing; renaming the conflicting database allowed the move');
  } finally {
    await removeDatabase(cells, x.id, projectId);
    await removeDatabase(cells, y.id, projectId);
  }
});


multiple('R12 a missed three-way home rotation converges without exposing placeholders', async ({ owner: { cells, projectId } }) => {
  const a = name('rotate-a');
  const b = name('rotate-b');
  const c = name('rotate-c');
  const x = await named(cells, home, a, ['gru', 'fra'], projectId);
  const y = await named(cells, home, b, ['gru', 'fra'], projectId);
  const z = await named(cells, home, c, ['gru', 'fra'], projectId);
  try {
    for (const row of [x, y, z]) {
      await seed(cells, row.id, projectId);
      await observed(cells, row.id, projectId);
    }
    expect(await Promise.all([x.id, y.id, z.id].map(id => recordedName(cells, id, projectId)))).toEqual([a, b, c]);
    const started = performance.now();
    await rename(cells, home, x.id, name('rotate-tmp'), projectId);
    await rename(cells, home, z.id, a, projectId);
    await rename(cells, home, y.id, c, projectId);
    await rename(cells, home, x.id, b, projectId);
    expect(performance.now() - started, 'the rotation finishes in less than one sweep').toBeLessThan(30_000);
    expect(await Promise.all([x.id, y.id, z.id].map(id => recordedName(cells, id, projectId))),
      'fra still has all original names and missed the intermediate name').toEqual([a, b, c]);
    await convergedNames(cells, [x.id, y.id, z.id], [a, b, c], [b, c, a], projectId);
    console.log('R12 fra converged to the three-way rotation within two sweeps; no GET exposed a placeholder');
  } finally {
    for (const row of [x, y, z]) await removeDatabase(cells, row.id, projectId);
  }
});

multiple('R13 promotion resolves a same-home name swap through placement before takeover', async ({ owner: { cells, projectId } }) => {
  const a = name('promote-a');
  const b = name('promote-b');
  const x = await named(cells, home, a, ['gru', 'fra'], projectId);
  const y = await named(cells, home, b, ['gru', 'fra'], projectId);
  try {
    for (const row of [x, y]) {
      await seed(cells, row.id, projectId);
      await observed(cells, row.id, projectId);
    }
    await rename(cells, home, x.id, name('promote-tmp'), projectId);
    await rename(cells, home, y.id, a, projectId);
    await rename(cells, home, x.id, b, projectId);
    expect(await Promise.all([x.id, y.id].map(id => recordedName(cells, id, projectId))),
      'target has not swept the swap before promotion').toEqual([a, b]);
    await answer(await patch(cells, x.id, ['fra'], projectId), 200, '200 promotion resolves the stale same-home holder');
    expect(await Promise.all([x.id, y.id].map(id => recordedName(cells, id, projectId)))).toEqual([b, a]);
    expect((await progress(cells, x.id, replica, projectId)).regions.filter(row => row.role === 'writer').map(row => row.region)).toEqual(['fra']);
    console.log('R13 same-home promotion resolved both names through the existing placement request before takeover');
  } finally {
    await removeDatabase(cells, x.id, projectId);
    await removeDatabase(cells, y.id, projectId);
  }
});
