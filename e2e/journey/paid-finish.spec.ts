import type { Page } from '@playwright/test';
import { deleteAccount } from '../support/account';
import { test, expect, type Audit } from '../support/audit';
import { branchAction, installAction, psql, sourceUrl, withoutPassword, workflowRun } from '../support/branch';
import { databaseNames, stripeLog, type PaidJourney } from '../support/paid';
import { runOperations, sdks } from '../support/sdk';
import { shell } from '../support/shell';
import { journey, setupFinished } from '../support/state';

test.describe.configure({ mode: 'default' });

const tsAuthDeletionCleanupInterval = 60_000;
const deletionReconcilerInterval = 30_000;
const deletionReconcilerPassMargin = 20_000;
const subscriptionCancelBound = tsAuthDeletionCleanupInterval + deletionReconcilerInterval + deletionReconcilerPassMargin;

test.beforeAll(({}, info) => {
  expect(setupFinished(info), 'setup did not finish').toBe(true);
});

async function databaseList(page: Page, audit: Audit, current: PaidJourney, names: string[], label: string): Promise<void> {
  await page.goto('/databases');
  expect((await databaseNames(page).allInnerTexts()).sort(), `the database list ${label}`).toEqual([...names].sort());
  await shell(page, current);
  await audit.check(`databases ${label}`);
}

test.describe('SDK group C', () => {
  for (const sdk of sdks) {
    test(`${sdk} SDK: a fork holds its source's rows and a scoped key is refused on another database of its project`, async () => {
      await runOperations(sdk, 'c');
    });
  }
});

test.describe('branch action', () => {
  test('the released branch action creates, reuses, resets and deletes a branch of the second database', async ({ page, audit }) => {
    const current = journey<PaidJourney>();
    installAction();
    const source = sourceUrl(current.projectId, current.secondDatabaseId);
    const rows = (url: string, password: string) => psql(url, password, 'SELECT id FROM action_check ORDER BY id');
    await psql(source, current.apiKey, 'CREATE TABLE action_check (id TEXT PRIMARY KEY)', "INSERT INTO action_check (id) VALUES ('source')");
    const sources = [current.databaseName, current.secondDatabaseName];

    const created = await branchAction('create', current.apiKey, current.secondDatabaseName, 'create');
    expect(Object.keys(created).sort(), 'outputs of create').toEqual(['api-url', 'connection-url', 'id', 'name', 'project-id']);
    expect(created.name).toMatch(/^pr-42-[0-9a-f]{24}$/);
    expect(created.id).toMatch(/^[0-7][0-9A-HJKMNP-TV-Z]{25}$/);
    expect(created['project-id']).toBe(current.projectId);
    expect(created['api-url']).toBe(`https://api.terrascale.tech/v1/projects/${current.projectId}/databases/${created.name}`);
    const branch = withoutPassword(created['connection-url']);
    expect(branch.url).toBe(`postgresql://any@pg.terrascale.tech:5432/${current.projectId}/${created.id}?sslmode=verify-full&sslrootcert=system`);
    expect(await rows(branch.url, branch.password), 'rows of the new branch').toEqual(['source']);
    await psql(branch.url, branch.password, "INSERT INTO action_check (id) VALUES ('branch')");
    expect(await rows(branch.url, branch.password), 'rows of the branch after its write').toEqual(['branch', 'source']);
    expect(await rows(source, current.apiKey), 'rows of the source after the write to the branch').toEqual(['source']);
    await databaseList(page, audit, current, [...sources, created.name], 'after create');

    const again = await branchAction('create', current.apiKey, current.secondDatabaseName, 'create-again');
    expect([again.name, again.id, again['project-id']], 'the second create reuses the branch').toEqual([created.name, created.id, current.projectId]);
    const reused = withoutPassword(again['connection-url']);
    expect(await rows(reused.url, reused.password), 'rows of the reused branch').toEqual(['branch', 'source']);
    await databaseList(page, audit, current, [...sources, created.name], 'after the second create');

    const reset = await branchAction('reset', current.apiKey, current.secondDatabaseName, 'reset');
    expect(reset.name).toBe(`g-${created.name.slice('pr-42-'.length)}-r${workflowRun}-a1`);
    expect(reset.id, 'reset makes a new database').not.toBe(created.id);
    const fresh = withoutPassword(reset['connection-url']);
    expect(await rows(fresh.url, fresh.password), 'rows of the reset branch').toEqual(['source']);
    await databaseList(page, audit, current, [...sources, created.name, reset.name], 'after reset');

    const deleted = await branchAction('delete', current.apiKey, current.secondDatabaseName, 'delete');
    expect(deleted, 'outputs of delete').toEqual({ name: created.name });
    expect(await rows(source, current.apiKey), 'rows of the source after delete').toEqual(['source']);
    await databaseList(page, audit, current, sources, 'after delete');
  });
});

test.describe('teardown', () => {
  test('the Pro owner deletes the account, which cancels the Stripe subscription, and a new sign-in is refused', async ({ page, audit, browser }, info) => {
    test.setTimeout(info.timeout + subscriptionCancelBound);
    const current = journey<PaidJourney>();
    await deleteAccount(page, audit, browser, info);
    await expect
      .poll(async () => (await stripeLog()).cancels.filter((cancel) => cancel.organization === current.organizationId).length, {
        message: 'subscriptions of the Pro organization the Stripe stand-in cancelled once the ts-auth deletion cleanup and the deletion reconciler ran',
        timeout: subscriptionCancelBound,
        intervals: [500],
      })
      .toBe(1);
    expect((await stripeLog()).unexpected, 'calls the Stripe stand-in answered with 404').toEqual([]);
  });
});
