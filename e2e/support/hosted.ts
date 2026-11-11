import { expect, type Page } from '@playwright/test';
import type { Audit } from './audit';
import { nextMail } from './inbox';
import { auth, dash } from './stack';
import { enterSecret } from './secret';

export const onHosted = (path: string) => (url: URL) => url.origin === auth && url.pathname === `/ui/terrascale/${path}`;

export async function openSignUp(page: Page, audit: Audit, label: string): Promise<void> {
  await page.goto(`${dash}/register`);
  await page.waitForURL(onHosted('sign-up'));
  await audit.check(`${label} sign-up`);
}

export async function submitSignUp(page: Page, audit: Audit, email: string, password: string, label: string): Promise<void> {
  await page.locator('#sign-up-email').fill(email);
  await enterSecret(page.locator('#sign-up-password'), password);
  await page.locator('form[action="/ui/terrascale/sign-up"] button[type=submit]').click();
  await expect(page).toHaveTitle('Verify your email · TerraScale');
  await audit.check(`${label} sign-up verify`);
}

export async function signUp(page: Page, audit: Audit, email: string, password: string, label: string): Promise<void> {
  await openSignUp(page, audit, label);
  await submitSignUp(page, audit, email, password, label);
}

export async function verify(page: Page, email: string): Promise<void> {
  const mail = await nextMail(email, 0);
  const code = /^\s*(\d{6})\s*$/m.exec(mail.text)?.[1] ?? '';
  expect(code, 'a 6-digit code on its own line').toMatch(/^\d{6}$/);
  await enterSecret(page.locator('#sign-up-code-digit'), code);
  await page.getByRole('button', { name: 'Verify email' }).click();
  await page.waitForURL((url) => url.origin === dash && url.pathname === '/onboarding');
}

export async function onboard(page: Page, audit: Audit, displayName: string, projectName: string, label: string): Promise<string> {
  await audit.check(`${label} onboarding account information`);
  await page.locator('#displayName').fill(displayName);
  await page.getByRole('button', { name: 'Save and continue' }).click();
  await expect(page.locator('button[data-plan="free"]')).toBeVisible();
  await audit.check(`${label} onboarding plan`);
  await page.locator('button[data-plan="free"]').click();
  await expect(page.locator('#projectName')).toBeVisible();
  await audit.check(`${label} onboarding project`);
  const region = await page.locator('#region').inputValue();
  await page.locator('#projectName').fill(projectName);
  await page.getByRole('button', { name: 'Create project and open Dashboard' }).click();
  await page.waitForURL((url) => url.origin === dash && url.pathname === '/');
  return region;
}

export async function signInWithPassword(page: Page, audit: Audit, email: string, password: string, label: string): Promise<void> {
  await page.waitForURL(onHosted('sign-in'));
  await audit.check(`${label} sign-in`);
  await page.locator('#sign-in-email').fill(email);
  await page.getByRole('button', { name: 'Use a password instead' }).click();
  await expect(page.locator('#sign-in-password')).toBeVisible();
  await audit.check(`${label} password sign-in`);
  await enterSecret(page.locator('#sign-in-password'), password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
}

export async function confirmWithPassword(page: Page, audit: Audit, password: string, label: string): Promise<void> {
  await page.waitForURL((url) => url.origin === auth && url.pathname.startsWith('/ui/terrascale/step-up/'));
  await audit.check(`${label} confirmation`);
  await enterSecret(page.locator('input[type=password]'), password);
  await page.locator('form button[type=submit]').first().click();
}
