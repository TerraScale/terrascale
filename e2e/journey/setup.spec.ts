import { randomBytes } from 'node:crypto';
import type { Page } from '@playwright/test';
import { test, expect } from '../support/audit';
import { cloudflareSendCount, newAddress, nextMail } from '../support/inbox';
import { saveJourney, saveSecret, saveSession, type SetupJourney } from '../support/state';
import { signInToken } from '../support/auth-management';
import { provisionProjectOwner } from '../support/project-owner';
import { shell } from '../support/shell';
import { hostedReady } from '../support/hosted';
import { regionsSettled } from '../support/overview';
import { auth, dash, onLocalStack } from '../support/stack';
import { enterSecret } from '../support/secret';

function stat(page: Page, label: string) {
  return page.locator('dl[aria-label="Workspace metrics"] .ts-stat').filter({ has: page.locator('dt', { hasText: label }) }).locator('dd');
}

const apiKeyFormat = /^(?:sk_)?[0-9A-HJKMNP-TV-Z]{26}_[0-9a-hjkmnp-tv-z]{52}$/;
const scopes = ['sql:read', 'sql:write', 'databases:read', 'databases:write', 'auth:read', 'auth:write'];

function reading(text: string, name: string, ...labels: string[]): number {
  const line = text.split('\n').find(row => (row.startsWith(`${name}{`) || row.startsWith(`${name} `)) && labels.every(label => row.includes(label)));
  if (line === undefined) throw new Error(`missing ${name} ${labels.join(' ')}`);
  const value = Number(line.slice(line.lastIndexOf(' ') + 1));
  expect(value, `${name} ${labels.join(' ')}`).toBeGreaterThanOrEqual(0);
  return value;
}

function each(text: string, name: string, label: string, values: readonly string[]) {
  for (const value of values) reading(text, name, `${label}="${value}"`);
}

const emailOutcomes = ['sent', 'failed', 'bounced', 'not_configured', 'capacity_refused', 'provider_exhausted', 'admission_unavailable', 'failover'] as const;
const signIns: readonly (readonly [string, string])[] = [
  ['hosted_code', 'rate_limited'], ['hosted_code', 'refused'], ['hosted_code', 'succeeded'],
  ['hosted_mfa', 'challenged'], ['hosted_mfa', 'expired'], ['hosted_mfa', 'rate_limited'], ['hosted_mfa', 'refused'], ['hosted_mfa', 'succeeded'],
  ['hosted_passkey', 'rate_limited'], ['hosted_passkey', 'refused'], ['hosted_passkey', 'expired'], ['hosted_passkey', 'succeeded'],
  ['hosted_password', 'rate_limited'], ['hosted_password', 'refused'], ['hosted_password', 'succeeded'],
  ['hosted_sign_up', 'rate_limited'], ['hosted_sign_up', 'refused'], ['hosted_sign_up', 'succeeded'],
  ['hosted_social', 'refused'], ['hosted_social', 'succeeded'],
  ['hosted_sso', 'rate_limited'], ['hosted_sso', 'refused'], ['hosted_sso', 'succeeded'],
];
const tokenGrants = ['authorization_code', 'refresh_token', 'client_credentials', 'token_exchange', 'other'] as const;
const tokenOutcomes = ['issued', 'invalid_request', 'invalid_client', 'invalid_grant', 'unauthorized_client', 'unsupported_grant_type', 'invalid_scope', 'temporarily_unavailable', 'server_error', 'other'] as const;
const mailPurposes = ['Authentication', 'Invitation', 'Alert', 'Allowance', 'Support', 'Test', 'Notification'] as const;

test('sign up, verify the email, onboard on Free, create a database and an API key', async ({ page, context, audit, browser }, info) => {
  if (info.project.name !== 'desktop-setup') {
    await onLocalStack(async () => {
      const [terrabase, authMetrics, platformMetrics] = await Promise.all([
        fetch('http://127.0.0.1:9091/metrics').then((response) => response.text()),
        fetch('http://127.0.0.1:9092/metrics').then((response) => response.text()),
        fetch('http://127.0.0.1:9093/metrics').then((response) => response.text()),
      ]);
      for (const name of [
        'terrabase_backup_disabled',
        'terrabase_caller_keys_expired',
        'terrabase_caller_keys_soonest_expiry_seconds',
        'terrabase_changefeed_subscribers',
        'terrabase_pressure_connections',
        'terrabase_pressure_in_flight',
        'terrabase_pressure_memory_budget_bytes',
        'terrabase_pressure_memory_used_bytes',
        'terrabase_pressure_requests_total',
      ]) reading(terrabase, name);
      reading(terrabase, 'terrabase_internal_authentication_requests_total', 'credential="peer"', 'caller="peer"');
      expect(platformMetrics.length, 'ts-platform metrics').toBeGreaterThan(0);
      each(authMetrics, 'ts_auth_emails_total', 'outcome', emailOutcomes);
      for (const [method, outcome] of signIns) reading(authMetrics, 'ts_auth_sign_ins_total', `method="${method}"`, `outcome="${outcome}"`);
      for (const grant of tokenGrants) {
        for (const outcome of tokenOutcomes) reading(authMetrics, 'ts_auth_tokens_total', `grant_type="${grant}"`, `outcome="${outcome}"`);
      }
      for (const purpose of mailPurposes) {
        for (const outcome of ['admitted', 'refused', 'held']) reading(authMetrics, 'ts_auth_email_admission_total', `purpose="${purpose}"`, `outcome="${outcome}"`);
      }
      each(authMetrics, 'ts_auth_deletion_cleanup_total', 'outcome', ['invalid', 'verified', 'storage_unavailable', 'cleanup_failed', 'batch_remaining', 'records_remaining', 'scope_not_deleted', 'project_handoff_required', 'organization_handoff_required']);
      const refusedBefore = reading(authMetrics, 'ts_auth_sign_ins_total', 'method="hosted_password"', 'outcome="refused"');
      const refused = await page.request.post(`${auth}/ui/terrascale/sign-in/password`, {
        headers: { origin: auth },
        form: { email: 'metric-zero@example.com', password: 'not-a-real-password' },
      });
      expect(refused.status()).toBe(401);
      const after = await (await fetch('http://127.0.0.1:9092/metrics')).text();
      expect(reading(after, 'ts_auth_sign_ins_total', 'method="hosted_password"', 'outcome="refused"')).toBe(refusedBefore + 1);
    });
  }
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
  await hostedReady(page);
  await expect(page).toHaveTitle('Create your account · TerraScale');
  await audit.check('sign-up');

  await page.locator('#sign-up-email').fill(email);
  await enterSecret(page.locator('#sign-up-password'), password);
  await page.locator('form:has(#sign-up-email) button[type=submit]').click();
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
  const onboardingRegions = page.locator('#onboarding-region');
  const onboardingPicked = onboardingRegions.locator('input[name="regions"]:checked');
  await expect(onboardingPicked).toHaveCount(1);
  const region = await onboardingPicked.inputValue();
  await onboardingPicked.click();
  await expect(onboardingPicked).toHaveCount(1);
  await expect(onboardingRegions.getByRole('status')).toHaveText('A database needs one region.');
  await page.locator('#projectName').fill(projectName);
  await expect(page.locator('#databaseName')).toHaveValue('main');
  await page.locator('#databaseName').fill(databaseName);
  await page.getByRole('button', { name: 'Create project and database' }).click();
  await page.waitForURL((url) => url.origin === dash && url.pathname === '/');
  const selection = new URL(page.url()).searchParams;
  const organizationId = selection.get('organizationId') ?? '';
  const projectId = selection.get('projectId') ?? '';
  const identity = { email, displayName, organizationId, organizationName: 'Personal', projectId, projectName };
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Overview');
  await regionsSettled(page);
  await expect(stat(page, 'Databases')).toHaveText('1');
  await expect(stat(page, 'Organization members')).toHaveText('1');
  await shell(page, identity);
  await audit.check('overview after onboarding');

  await page.goto('/databases');
  const databaseRow = page.locator('#project-databases tbody tr').filter({ hasText: databaseName });
  await expect(databaseRow).toBeVisible();
  const link = databaseRow.getByRole('link');
  await expect(link).toHaveAttribute('href', /^\/databases\/[0-9A-HJKMNP-TV-Z]{26}(\?|$)/);
  const databaseId = new URL(await link.getAttribute('href') ?? '', page.url()).pathname.split('/')[2];
  await page.getByRole('button', { name: 'New database', exact: true }).first().click();
  await expect(page.locator('#create-database[open]')).toBeVisible();
  await audit.check('new database dialog');
  const regions = page.locator('#create-database-regions');
  const picked = regions.locator('input[name="regions"]:checked');
  await expect(picked).toHaveValue(new RegExp(`^${region}$`, 'i'));
  await picked.click();
  await expect(picked).toHaveCount(1);
  await expect(regions.getByRole('status')).toHaveText('A database needs one region.');
  await page.locator('#create-database').getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.locator('dialog:modal')).toHaveCount(0);
  await link.click();
  await page.waitForURL(url => url.pathname === `/databases/${databaseId}`);
  await expect(page.locator('.ts-db-title')).toHaveText(databaseName);
  await expect(page.locator('main').getByText('ready', { exact: true }).first()).toBeVisible();
  await expect(page.locator('main').getByText(new RegExp(`^${region}$`, 'i')).first()).toBeVisible();
  await audit.check('database created');

  await page.goto(`/databases/${databaseId}/replication`);
  const placed = page.locator('#database-regions input[name="regions"]:checked');
  await expect(placed).toHaveValue(new RegExp(`^${region}$`, 'i'));
  await placed.press('Space');
  await expect(placed).toHaveCount(1);
  await expect(page.locator('#database-regions').getByRole('status')).toHaveText('A database needs one region.');
  const saved = page.waitForResponse((response) => response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/v1/dashboard/databases/placement');
  await page.getByRole('button', { name: 'Save regions', exact: true }).click();
  const placementResponse = await saved;
  expect(placementResponse.status(), 'saving regions in place').toBe(200);
  await page.waitForLoadState('load');
  await expect(placed).toHaveValue(new RegExp(`^${region}$`, 'i'));
  await audit.check('database regions saved');

  await page.goto('/api-keys?create=key');
  const keyDialog = page.locator('#create-key[open]');
  await expect(keyDialog).toBeVisible();
  await page.locator('#key-name').fill(apiKeyName);
  await keyDialog.getByRole('radio', { name: 'Full access', exact: true }).check();
  for (const scope of scopes) {
    const box = keyDialog.locator(`fieldset input[name="scopes"][value="${scope}"]`);
    await expect(box).toBeChecked();
    await expect(box).toBeDisabled();
  }
  await audit.check('new API key dialog');
  await keyDialog.getByRole('button', { name: 'Create key', exact: true }).click();
  const secretDialog = page.locator('#key-secret');
  await expect(secretDialog).toBeVisible();
  const masked = secretDialog.locator('.ts-apikeys-secret-mask');
  await expect(masked).toBeVisible();
  const secret = page.locator('#api-key-secret');
  const apiKey = (await secret.textContent() ?? '').trim();
  await expect(masked).not.toHaveText(apiKey);
  await expect(masked).toContainText('…');
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  await secretDialog.getByRole('button', { name: 'Show key', exact: true }).click();
  await expect(secret).toBeVisible();
  await expect(secret).toHaveText(apiKey);
  await secretDialog.getByRole('button', { name: 'Copy API key', exact: true }).click();
  await expect(secretDialog.getByRole('button', { name: 'Copied', exact: true })).toBeVisible();
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(apiKey);
  saveSecret('api-key', apiKey);
  expect(apiKeyFormat.test(apiKey), 'the API key secret has the canonical format').toBe(true);
  await audit.check('API key created');
  await page.reload();
  const keyRow = page.locator('main tr').filter({ hasText: apiKeyName });
  await expect(keyRow).toHaveCount(1);
  await expect(keyRow.locator('.ts-apikeys-access')).toHaveText(['Full']);
  expect((await page.content()).includes(apiKey), 'the API key secret is gone after a reload').toBe(false);
  await shell(page, identity);
  await audit.check('API keys after reload');
  const ownerToken = await signInToken(page.request, 'setup-owner');
  const userId = JSON.parse(Buffer.from(ownerToken.split('.')[1], 'base64url').toString()).sub as string;
  const minter = await page.request.post(`${auth}/v1/terrascale/organizations/${organizationId}/projects/${projectId}/api-keys`, {
    headers: { Authorization: 'Bearer ' + ownerToken }, data: { name: `journey-mint-${runId}`, scopes: ['auth:tokens:mint'] },
  });
  expect(minter.status()).toBe(201);
  const mintKey = (await minter.json()).key as string;
  saveSecret('journey-mint-key', mintKey);

  await saveSession(context);
  saveJourney<SetupJourney>({
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
    userId,
    mintKey,
  });
  if (info.project.name === 'setup') await provisionProjectOwner(browser, info);
});
