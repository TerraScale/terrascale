import { randomBytes } from 'node:crypto';
import type { Page } from '@playwright/test';
import { test, expect } from '../support/audit';
import { cloudflareSendCount, newAddress, nextMail } from '../support/inbox';
import { saveJourney, saveSecret, saveSession } from '../support/state';
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
  await onLocalStack(async () => {
    const terrabase = await (await fetch('http://127.0.0.1:9091/metrics')).text();
    const authMetrics = await (await fetch('http://127.0.0.1:9092/metrics')).text();
    for (const name of [
      'terrabase_backup_holes', 'terrabase_backup_failures_total', 'terrabase_backup_holes_recorded_total', 'terrabase_backup_generations_total',
      'terrabase_cdc_unshipped_discarded_total', 'terrabase_storage_writers_evicted_total', 'terrabase_storage_migration_failures_total',
      'terrabase_storage_fault_publication_late_total', 'terrabase_disk_refused_total', 'terrabase_storage_rejected_total',
      'terrabase_qos_session_admissions_total', 'terrabase_qos_preauthentication_admissions_total', 'terrabase_qos_relay_output_timeouts_total',
      'terrabase_request_logs_dropped_total', 'terrabase_usage_observations_dropped_total', 'terrabase_catalog_name_kept_total',
      'terrabase_clock_observe_capped_total', 'terrabase_clock_steps_total', 'terrabase_clock_failed_rounds_total',
      'terrabase_localack_finished_total', 'terrabase_localack_unplaced_total', 'terrabase_shape_reads_total', 'terrabase_shape_flights_started_total',
      'terrabase_shape_seals_total', 'terrabase_shape_sealed_bytes_total', 'terrabase_shape_redirects_total', 'terrabase_shape_sweep_deleted_total',
    ]) reading(terrabase, name);
    each(terrabase, 'terrabase_backup_uploaded_bytes_total', 'kind', ['snapshot', 'segment', 'lineage', 'hole']);
    each(terrabase, 'terrabase_backup_generation_restarts_total', 'reason', ['lineage', 'key_lost', 'log_unplaceable']);
    each(terrabase, 'terrabase_backup_snapshots_deferred_total', 'reason', ['backlog', 'busy']);
    each(terrabase, 'terrabase_backup_drills_total', 'result', ['passed', 'failed']);
    each(terrabase, 'terrabase_backup_bucket_snapshots_total', 'result', ['taken', 'deleted', 'failed']);
    each(terrabase, 'terrabase_storage_reads_total', 'path', ['point', 'query', 'shape', 'shape_body', 'shape_sweep']);
    each(terrabase, 'terrabase_storage_rewrite_refused_total', 'reason', ['Safety', 'Budget', 'Deadline', 'Pressure']);
    each(terrabase, 'terrabase_qos_session_refusals_total', 'kind', ['organization_cap', 'process_ceiling', 'relay_ceiling', 'preauthentication_allowance']);
    for (const credential of ['caller_rejected', 'unmatched', 'legacy', 'peer']) {
      reading(terrabase, 'terrabase_internal_authentication_requests_total', `credential="${credential}"`, 'caller="peer"');
    }
    each(terrabase, 'terrabase_operator_legacy_grants_total', 'kind', ['act_as', 'data']);
    reading(terrabase, 'terrabase_sql_connections_total', 'region=');
    for (const route of ['local', 'home']) {
      for (const reason of ['client_key', 'update_by_key', 'delete_by_key', 'read', 'ddl', 'autoincrement', 'rowid_key', 'unique', 'unique_change', 'fk_change', 'fk_parent_delete', 'relative_update', 'not_by_key', 'complex', 'trigger', 'read_then_write', 'remembered', 'override', 'unknown']) {
        for (const commit of ['local', 'home']) {
          reading(terrabase, 'terrabase_sql_write_routes_total', `route="${route}"`, `reason="${reason}"`, `commit="${commit}"`, 'region=');
        }
      }
    }
    reading(terrabase, 'terrabase_stats_dropped_total', 'kind="database"');
    reading(terrabase, 'terrabase_project_closure_forward_errors_total', 'error="failed"');
    each(terrabase, 'terrabase_clock_stamps_total', 'outcome', ['issued', 'refused']);
    each(terrabase, 'terrabase_localack_unreadable_total', 'reason', ['decrypt', 'decode']);
    each(terrabase, 'terrabase_auth_stored_keys_rejected_total', 'reason', ['invalid', 'clock_backwards', 'expired']);
    each(terrabase, 'terrabase_auth_key_fetches_total', 'outcome', ['success', 'failure']);
    for (const cache of ['tenants', 'records', 'plans', 'placements', 'registrations']) {
      for (const result of ['hit', 'miss']) reading(terrabase, 'terrabase_catalog_cache_lookups_total', `cache="${cache}"`, `result="${result}"`);
    }
    each(terrabase, 'terrabase_purge_errors_total', 'reason', ['active_cleanup', 'record', 'file_cleanup']);
    each(terrabase, 'terrabase_shape_sweep_handles_total', 'result', ['failed', 'refused', 'current', 'retired', 'unclassified', 'malformed', 'unknown']);
    each(terrabase, 'terrabase_shape_cdn_purge_attempts_total', 'outcome', ['accepted', 'failed']);
    each(terrabase, 'terrabase_shape_cdn_purges_total', 'outcome', ['dropped', 'unavailable', 'verification_failed', 'purged', 'failed']);
    each(terrabase, 'terrabase_shape_cdn_requests_total', 'cache_status', ['hit', 'miss', 'other']);
    each(authMetrics, 'ts_auth_emails_total', 'outcome', emailOutcomes);
    for (const [method, outcome] of signIns) reading(authMetrics, 'ts_auth_sign_ins_total', `method="${method}"`, `outcome="${outcome}"`);
    for (const grant of tokenGrants) {
      for (const outcome of tokenOutcomes) reading(authMetrics, 'ts_auth_tokens_total', `grant_type="${grant}"`, `outcome="${outcome}"`);
    }
    for (const purpose of mailPurposes) {
      for (const outcome of ['admitted', 'refused']) reading(authMetrics, 'ts_auth_email_admission_total', `purpose="${purpose}"`, `outcome="${outcome}"`);
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
  const region = await page.locator('#onboarding-region input[name="region"]:checked').inputValue();
  await page.locator('#projectName').fill(projectName);
  await page.getByRole('button', { name: 'Create project and open Dashboard' }).click();
  await page.waitForURL((url) => url.origin === dash && url.pathname === '/');
  const selection = new URL(page.url()).searchParams;
  const organizationId = selection.get('organizationId') ?? '';
  const projectId = selection.get('projectId') ?? '';
  const identity = { email, displayName, organizationId, organizationName: 'Personal', projectId, projectName };
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Overview');
  await regionsSettled(page);
  await expect(stat(page, 'Databases')).toHaveText('0');
  await expect(stat(page, 'Organization members')).toHaveText('1');
  await shell(page, identity);
  await audit.check('overview after onboarding');

  await page.goto('/databases');
  await page.getByRole('button', { name: 'New database', exact: true }).first().click();
  await expect(page.locator('#create-database[open]')).toBeVisible();
  await audit.check('new database dialog');
  const regions = page.locator('#create-database-regions');
  const picked = regions.locator('input[name="regions"]:checked');
  await expect(picked).toHaveValue(new RegExp(`^${region}$`, 'i'));
  await picked.click();
  await expect(picked).toHaveCount(1);
  await expect(regions.getByRole('status')).toHaveText('A database needs one region.');
  await page.locator('#database-name').fill(databaseName);
  await page.locator('#create-database form').getByRole('button', { name: 'Create database', exact: true }).click();
  await page.waitForURL(/\/databases\/[0-9A-HJKMNP-TV-Z]{26}(\?|$)/);
  const databaseId = new URL(page.url()).pathname.split('/')[2];
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(databaseName);
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
  expect(placementResponse.status(), placementResponse.status() === 303 ? 'saving the same regions' : `saving the same regions: ${placementResponse.status()} ${await placementResponse.text()}`).toBe(303);
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
  if (info.project.name === 'setup') await provisionProjectOwner(browser, info);
});
