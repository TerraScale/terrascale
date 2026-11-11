import { randomBytes } from 'node:crypto';
import type { Page } from '@playwright/test';
import { test, expect } from '../support/audit';
import { cloudflareSendCount, newAddress, nextMail } from '../support/inbox';
import { saveJourney, saveSecret, saveSession } from '../support/state';
import { shell } from '../support/shell';
import { auth, dash, onLocalStack } from '../support/stack';
import { enterSecret } from '../support/secret';

function stat(page: Page, label: string) {
  return page.locator('dl[aria-label="Workspace metrics"] .ts-stat').filter({ has: page.locator('dt', { hasText: label }) }).locator('dd');
}

const apiKeyFormat = /^[0-9A-HJKMNP-TV-Z]{26}_[0-9a-hjkmnp-tv-z]{52}$/;
const scopes = ['sql:read', 'sql:write', 'databases:read', 'databases:write'];

test('sign up, verify the email, onboard on Free, create a database and an API key', async ({ page, context, audit }) => {
  const runId = randomBytes(4).toString('hex');
  const email = await newAddress(`journey-${runId}`);
  const password = randomBytes(24).toString('base64url');
  saveSecret('password', password);
  const displayName = `Journey ${runId}`;
  const projectName = `Journey project ${runId}`;
  const databaseName = `journey-${runId}`;
  const apiKeyName = `journey-key-${runId}`;

  await page.goto('/register');
  await expect(page).toHaveURL((url) => url.origin === auth && url.pathname === '/ui/terrascale/sign-up');
  await expect(page).toHaveTitle('Create your account · TerraScale');
  await audit.check('sign-up');

  await page.locator('#sign-up-email').fill(email);
  await enterSecret(page.locator('#sign-up-password'), password);
  await page.locator('form[action="/ui/terrascale/sign-up"] button[type=submit]').click();
  await expect(page).toHaveTitle('Verify your email · TerraScale');
  await expect(page.locator('form:has(#sign-up-code-digit) input[type=hidden][name=pending_token]')).toHaveCount(1);
  await audit.check('sign-up verify');

  const mail = await nextMail(email, 0);
  const code = /^\s*(\d{6})\s*$/m.exec(mail.text)?.[1] ?? '';
  expect(code, 'a 6-digit code on its own line').toMatch(/^\d{6}$/);
  expect(mail.from).toBe('no-reply@terrascale.tech');
  expect(mail.subject.replace(code, '<code>')).toBe('<code> is your TerraScale verification code');
  await onLocalStack(async () => expect(await cloudflareSendCount(), 'messages sent through the Cloudflare fallback').toBe(0));

  await enterSecret(page.locator('#sign-up-code-digit'), code);
  await page.getByRole('button', { name: 'Verify email' }).click();
  await page.waitForURL((url) => url.origin === dash && url.pathname === '/onboarding');
  await expect(page).toHaveTitle('Activate your TerraScale account · TerraScale');
  await expect(page.locator('#accountEmail')).toHaveValue(email);
  await audit.check('onboarding account information');

  await page.locator('#displayName').fill(displayName);
  await page.getByRole('button', { name: 'Save and continue' }).click();
  await expect(page.locator('button[data-plan="free"]')).toBeVisible();
  await audit.check('onboarding plan');

  await page.locator('button[data-plan="free"]').click();
  await expect(page.locator('#projectName')).toBeVisible();
  await audit.check('onboarding project');
  const region = await page.locator('#region').inputValue();
  await page.locator('#projectName').fill(projectName);
  await page.getByRole('button', { name: 'Create project and open Dashboard' }).click();
  await page.waitForURL((url) => url.origin === dash && url.pathname === '/');
  const selection = new URL(page.url()).searchParams;
  const organizationId = selection.get('organizationId') ?? '';
  const projectId = selection.get('projectId') ?? '';
  const identity = { email, displayName, organizationId, organizationName: 'Personal', projectId, projectName };
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Overview');
  await expect(stat(page, 'Databases')).toHaveText('0');
  await expect(stat(page, 'Organization members')).toHaveText('1');
  await shell(page, identity);
  await audit.check('overview after onboarding');

  await page.goto('/databases');
  await page.getByRole('button', { name: 'New database', exact: true }).first().click();
  await expect(page.locator('#create-database[open]')).toBeVisible();
  await audit.check('new database dialog');
  await page.locator('#database-name').fill(databaseName);
  await page.locator('#create-database form').getByRole('button', { name: 'Create database', exact: true }).click();
  await page.waitForURL(/\/databases\/[0-9A-HJKMNP-TV-Z]{26}(\?|$)/);
  const databaseId = new URL(page.url()).pathname.split('/')[2];
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(databaseName);
  await expect(page.locator('main').getByText('ready', { exact: true }).first()).toBeVisible();
  await expect(page.locator('main').getByText(new RegExp(`^${region}$`, 'i')).first()).toBeVisible();
  await audit.check('database created');

  await page.goto('/api-keys?create=key');
  const keyDialog = page.locator('#create-key[open]');
  await expect(keyDialog).toBeVisible();
  await page.locator('#key-name').fill(apiKeyName);
  await keyDialog.getByRole('checkbox', { name: /^Full access/ }).check();
  for (const scope of scopes) {
    const box = keyDialog.locator(`fieldset input[name="scopes"][value="${scope}"]`);
    await expect(box).toBeChecked();
    await expect(box).toBeDisabled();
  }
  await audit.check('new API key dialog');
  await keyDialog.getByRole('button', { name: 'Create key', exact: true }).click();
  const secret = page.locator('.ts-apikeys-secret-value');
  await expect(secret).toBeVisible();
  const apiKey = (await secret.innerText()).trim();
  saveSecret('api-key', apiKey);
  expect(apiKeyFormat.test(apiKey), 'the API key secret has the canonical format').toBe(true);
  await audit.check('API key created');
  await page.reload();
  const keyRow = page.locator('main tr').filter({ hasText: apiKeyName });
  await expect(keyRow).toHaveCount(1);
  await expect(keyRow.locator('.ts-apikeys-scopes .ts-badge')).toHaveText(['Full access']);
  expect((await page.content()).includes(apiKey), 'the API key secret is gone after a reload').toBe(false);
  await shell(page, identity);
  await audit.check('API keys after reload');

  await saveSession(context);
  saveJourney({
    runId,
    email,
    password,
    displayName,
    organizationId,
    organizationName: 'Personal',
    projectId,
    projectName,
    databaseId,
    databaseName,
    databaseRegion: region,
    apiKeyName,
    apiKey,
  });
});
