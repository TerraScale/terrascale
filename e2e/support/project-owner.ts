import { randomBytes } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import type { Browser, TestInfo } from '@playwright/test';
import { Audit, auditedContext, expect } from './audit';
import { signInToken, stepUp, type AuthOwner } from './auth-management';
import { onboard, signUp, verify } from './hosted';
import { newAddress } from './inbox';
import { auth, desktop, stateDir } from './stack';
import { saveCookies, saveSecret } from './state';

type ProjectOwner = AuthOwner & { email: string; apiKey: string };

const identityFile = join(stateDir, 'auth-features.journey.json');
const sessionFile = join(stateDir, 'auth-features.session.json');

export async function provisionProjectOwner(browser: Browser, info: TestInfo): Promise<void> {
  const label = 'Auth features owner';
  const runId = randomBytes(6).toString('hex');
  const email = await newAddress(label.toLowerCase().replaceAll(' ', '-') + '-' + runId);
  const password = randomBytes(24).toString('base64url');
  saveSecret(label + '-password', password);
  const context = await auditedContext(browser, { viewport: desktop, storageState: { cookies: [], origins: [] } });
  const page = await context.newPage();
  const audit = new Audit(page, info);
  await signUp(page, audit, email, password, label);
  await verify(page, email);
  await onboard(page, audit, label, label + ' ' + runId, label);
  const url = new URL(page.url());
  const projectId = url.searchParams.get('projectId')!;
  const organizationId = url.searchParams.get('organizationId')!;
  expect(projectId).toMatch(/^[0-9A-HJKMNP-TV-Z]{26}$/);
  expect(organizationId).toMatch(/^[0-9A-HJKMNP-TV-Z]{26}$/);
  await page.goto('/api-keys?create=key');
  const dialog = page.locator('#create-key[open]');
  await dialog.locator('#key-name').fill(label);
  await dialog.getByRole('radio', { name: 'Full access', exact: true }).check();
  await dialog.getByRole('button', { name: 'Create key', exact: true }).click();
  const apiKey = (await page.locator('.ts-apikeys-secret-value').innerText()).trim();
  saveSecret(label + '-api-key', apiKey);
  const current = { runId, email, password, projectId, organizationId, apiKey };
  writeFileSync(identityFile, JSON.stringify(current), { mode: 0o600 });
  await context.storageState({ path: sessionFile });
  await audit.check('auth feature owner provisioned');
  await saveCookies(context);
  await context.close();
}

export async function createProjectOwner(browser: Browser, info: TestInfo) {
  const current = JSON.parse(readFileSync(identityFile, 'utf8')) as ProjectOwner;
  const context = await auditedContext(browser, { viewport: desktop, storageState: sessionFile });
  const page = await context.newPage();
  const audit = new Audit(page, info);
  const close = async () => {
    await saveCookies(context);
    await context.close();
  };
  return { current, context, page, audit, close };
}

export async function deleteProjectOwner(browser: Browser, info: TestInfo): Promise<void> {
  const account = await createProjectOwner(browser, info);
  const { current, page } = account;
  try {
    const token = await signInToken(page.request, 'auth-features-cleanup');
    const proof = await stepUp(page.request, token, 'delete_account', current.password);
    const deleted = await page.request.delete(auth + '/v1/terrascale/account', {
      headers: { Authorization: 'Bearer ' + token, 'X-TerraScale-Step-Up-Proof': proof },
    });
    expect(deleted.status()).toBe(204);
  } finally {
    await account.close();
  }
}
