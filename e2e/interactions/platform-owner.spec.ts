import { randomBytes } from 'node:crypto';
import type { APIRequestContext, Locator, Page } from '@playwright/test';
import { Audit, auditedContext, expect, test } from '../support/audit';
import { signInToken } from '../support/auth-management';
import { hostedReady, onHosted } from '../support/hosted';
import { mailCount, nextMail } from '../support/inbox';
import { regionsSettled } from '../support/overview';
import { enterSecret } from '../support/secret';
import { shell } from '../support/shell';
import { auth, dash, desktop, production } from '../support/stack';
import { saveCookies, saveSecret } from '../support/state';

const owner = { email: 'mariogk@terrascale.tech', name: 'Mario Gabriell Karaziaki' };
const organization = { id: '00000000000000000000000001', name: 'TerraScale' };
const authProject = { id: '00000000000000000000000002', databaseId: '00000000000000000000000003', name: 'ts-auth' };
const dashboardProject = { id: '00000000000000000000000005', databaseId: '00000000000000000000000004', name: 'ts-dashboard' };
const platformProject = { id: '00000000000000000000000006', databaseId: '00000000000000000000000007', name: 'ts-platform' };
const platformProjects = [authProject, dashboardProject, platformProject];
const organizationApi = `${auth}/v1/terrascale/organizations/${organization.id}`;

test.skip(production, 'the platform owner passcode is read from the local Mailpit, and the production owner is never signed in');

async function signInByEmailCode(page: Page, audit: Audit): Promise<void> {
  const seen = await mailCount(owner.email);
  await page.goto(`${dash}/login`);
  await page.waitForURL(onHosted('sign-in'));
  await hostedReady(page);
  await audit.check('platform owner sign-in');
  await page.locator('#sign-in-email').fill(owner.email);
  await page.getByRole('button', { name: 'Email me a sign-in code', exact: true }).click();
  await expect(page.locator('#sign-in-code-digit')).toBeVisible();
  await audit.check('platform owner sign-in code');
  const code = /^\s*(\d{6})\s*$/m.exec((await nextMail(owner.email, seen)).text)?.[1] ?? '';
  expect(code, 'a 6-digit code on its own line').toMatch(/^\d{6}$/);
  saveSecret('platform-owner-code', code);
  await enterSecret(page.locator('#sign-in-code-digit'), code);
  await page.getByRole('button', { name: 'Verify and sign in', exact: true }).click();
  await page.waitForURL((url) => url.origin === dash && url.pathname === '/');
}

async function projectIds(request: APIRequestContext, headers: Record<string, string>): Promise<{ id: string; name: string }[]> {
  const listed = await request.get(`${organizationApi}/projects?limit=100`, { headers });
  expect(listed.status()).toBe(200);
  return ((await listed.json()) as { items: { id: string; name: string }[] }).items;
}

const fact = (scope: Locator, label: string) => scope.locator(`dt:text-is("${label}") + dd`);

async function counted(figure: Locator): Promise<number> {
  await expect(figure).toHaveAttribute('title', /^[\d,]+$/);
  return Number((await figure.getAttribute('title'))!.replaceAll(',', ''));
}

test('the platform owner signs in by email code into the TerraScale organization on the unlimited TerraScale plan', async ({ browser }, info) => {
  const context = await auditedContext(browser, { viewport: desktop, storageState: { cookies: [], origins: [] } });
  try {
    const page = await context.newPage();
    const audit = new Audit(page, info);
    const navigated: string[] = [];
    page.on('request', (request) => {
      if (request.isNavigationRequest()) navigated.push(new URL(request.url()).pathname);
    });

    await signInByEmailCode(page, audit);
    expect(navigated, 'the platform owner is never sent to onboarding').not.toContain('/onboarding');
    await page.goto('/onboarding');
    await page.waitForURL((url) => url.origin === dash && url.pathname === '/');
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Overview');
    await regionsSettled(page);
    await shell(page, {
      email: owner.email,
      displayName: owner.name,
      organizationId: organization.id,
      organizationName: organization.name,
      projectId: authProject.id,
      projectName: authProject.name,
    });
    await expect(page.locator('[data-organization-option]')).toHaveCount(1);
    await expect(page.locator('[data-organization-option]')).toHaveAttribute('data-organization-option', organization.id);
    await audit.check('platform owner overview');

    const token = await signInToken(page.request, 'platform-owner');
    const headers = { Authorization: 'Bearer ' + token };
    const organizations = await page.request.get(`${auth}/v1/terrascale/organizations?limit=100`, { headers });
    expect(organizations.status()).toBe(200);
    const memberships = ((await organizations.json()) as { items: { id: string; name: string; role: string }[] }).items;
    expect(memberships.map(({ id, name, role }) => ({ id, name, role })), 'no Personal organization was created')
      .toEqual([{ ...organization, role: 'owner' }]);

    await page.goto('/organization');
    await expect(page.getByRole('heading', { level: 1 })).toHaveText(organization.name);
    await expect(page.locator('#organization-id')).toHaveText(organization.id);
    await expect(page.locator('.ts-org-title-row')).toContainText('TerraScale plan');
    const planCard = page.locator('section[aria-labelledby="overview-billing-title"]');
    for (const label of ['Databases', 'Storage', 'Team members', 'Operation units']) {
      await expect(fact(planCard, label)).toHaveText('Unlimited');
    }
    await expect(fact(page.locator('#organization-details'), 'Your role')).toHaveText('Organization Owner');
    await audit.check('platform organization');

    await page.goto('/organization?tab=projects');
    const rows = page.locator('#projects table.ts-table > tbody > tr');
    await expect(rows).toHaveCount(platformProjects.length);
    for (const project of platformProjects) {
      const row = rows.filter({ hasText: project.id });
      await expect(row.locator('.ts-org-project-name .ts-table-strong')).toHaveText(project.name);
      await expect(row.locator('td[data-label="Databases"]')).toHaveText('1');
    }
    await audit.check('platform projects');
    await expect(page.getByRole('button', { name: `Delete ${authProject.name}`, exact: true })).toHaveCount(0);
    for (const project of [dashboardProject, platformProject]) {
      await page.getByRole('button', { name: `Delete ${project.name}`, exact: true }).click();
      const dialog = page.locator(`#delete-project-${project.id}`);
      await expect(dialog).toContainText('Project still has 1 database');
      await expect(dialog.getByRole('button', { name: 'Delete project', exact: true })).toBeDisabled();
      await dialog.getByRole('button', { name: 'Close', exact: true }).click();
      await expect(dialog).not.toBeVisible();
    }
    for (const project of platformProjects) {
      const refused = await page.request.delete(`${organizationApi}/projects/${project.id}`, { headers });
      expect(refused.status(), `deleting ${project.name}`).toBe(409);
      expect(((await refused.json()) as { error: { code: string } }).error.code).toBe('platform_project_protected');
    }

    for (const project of platformProjects) {
      await page.goto(`/databases?organizationId=${organization.id}&projectId=${project.id}`);
      await expect(page.locator(`[data-active-project-id="${project.id}"]`)).toHaveText(`Current project: ${project.name}`);
      const database = page.locator('#project-databases tbody tr');
      await expect(database).toHaveCount(1);
      await expect(database.locator('.ts-db-name .ts-strong')).toHaveText(project.name);
      await expect(database.getByRole('link')).toHaveAttribute('href', new RegExp(`^/databases/${project.databaseId}(\\?|$)`));
      await expect(database.locator('.ts-badge')).toHaveText('ready');
      await audit.check(`platform database ${project.name}`);
    }

    await page.goto('/organization?tab=billing');
    const summary = page.locator('#billing-summary');
    await expect(summary.locator('.ts-billing-plan-name')).toHaveText('TerraScale');
    await expect(summary.locator('.ts-billing-plan .ts-badge')).toHaveText('Active');
    await expect(fact(summary, 'Allowances')).toHaveText('Unlimited');
    await expect(fact(summary, 'Stripe charges')).toHaveText('None');
    await expect(page.locator('#available-plans')).toHaveCount(0);
    await expect(page.locator('main').getByText('Available plans', { exact: true })).toHaveCount(0);
    await expect(page.locator('main').getByRole('button', { name: /^(Choose|Change plan|Upgrade to|Switch to|Manage payment)/ })).toHaveCount(0);
    await audit.check('TerraScale plan billing');

    await page.goto(`/usage?organizationId=${organization.id}&projectId=${authProject.id}`);
    await expect(page.locator('#usage-allowance-title')).toHaveText('TerraScale plan allowance');
    const meter = (name: string) => page.locator('#usage-allowance .ts-usage-meter').filter({ has: page.locator(`dt:text-is("${name}")`) });
    for (const name of ['Operation units this month', 'Function invocations this month', 'Storage across regions', 'Client egress this month']) {
      await expect(meter(name).locator('.ts-usage-meter-limit')).toHaveText('Unlimited');
    }
    await expect(meter('SQL reads this month').locator('.ts-usage-meter-limit')).toHaveText('Not limited');
    for (const name of ['Operation units this month', 'Function invocations this month', 'SQL reads this month', 'Storage across regions']) {
      await expect(meter(name).locator('.ts-usage-meter-value')).toHaveAttribute('data-measured', 'true');
    }
    expect(await counted(meter('Operation units this month').locator('.ts-usage-meter-value')), 'metered operation units').toBeGreaterThan(0);
    const shares = page.locator('#usage-by-project .ts-usage-share > li');
    await expect(shares).toHaveCount(platformProjects.length);
    for (const project of platformProjects) {
      const share = shares.filter({ hasText: project.name });
      await expect(share).toContainText('1 database');
      await expect(share.locator('.ts-usage-share-value')).toHaveAttribute('data-measured', 'true');
    }
    await expect(page.locator('#usage-allowance [role="alert"], #usage-summary [role="alert"]')).toHaveCount(0);
    await audit.check('TerraScale plan usage');
    for (const project of [authProject, dashboardProject]) {
      await page.goto(`/usage?tab=overview&project=${project.id}&database=`);
      const stats = page.locator('dl[aria-label="Project usage summary"] .ts-stat');
      const writes = stats.filter({ has: page.locator('dt:text-is("Writes")') }).locator('dd');
      expect(await counted(writes), `metered writes of ${project.name}`).toBeGreaterThan(0);
      await expect(page.locator('#usage-allowance [role="alert"], #usage-summary [role="alert"]')).toHaveCount(0);
      await audit.check(`platform usage of ${project.name}`);
    }

    const temporary = `platform-temp-${randomBytes(4).toString('hex')}`;
    try {
      await page.goto(`/organization?tab=projects&organizationId=${organization.id}&projectId=${authProject.id}`);
      await page.locator('#projects').getByRole('button', { name: 'Create project', exact: true }).click();
      const create = page.locator('dialog#create-project');
      await create.locator('input[name="name"]').fill(temporary);
      await create.getByRole('button', { name: 'Create project', exact: true }).click();
      await expect(rows).toHaveCount(platformProjects.length + 1);
      const row = rows.filter({ hasText: temporary });
      const projectId = ((await row.locator('.ts-org-project-name .ts-mono').textContent()) ?? '').trim();
      expect(projectId).toMatch(/^[0-9A-HJKMNP-TV-Z]{26}$/);
      await audit.check('temporary project in the platform organization');
      await row.getByRole('button', { name: `Delete ${temporary}`, exact: true }).click();
      const deletion = page.locator(`#delete-project-${projectId}`);
      await deletion.locator(`#delete-project-confirmation-${projectId}`).fill(temporary);
      await deletion.getByRole('button', { name: 'Delete project', exact: true }).click();
      await expect(rows).toHaveCount(platformProjects.length);
      await expect(deletion).toHaveCount(0);
      await audit.check('temporary project deleted');
    } finally {
      for (const left of (await projectIds(page.request, headers)).filter((project) => project.name === temporary)) {
        const removed = await page.request.delete(`${organizationApi}/projects/${left.id}`, { headers });
        expect([202, 204]).toContain(removed.status());
      }
    }
    expect((await projectIds(page.request, headers)).map((project) => project.id).sort()).toEqual(platformProjects.map((project) => project.id));
    await audit.finish();
  } finally {
    await saveCookies(context);
    await context.close();
  }
});
