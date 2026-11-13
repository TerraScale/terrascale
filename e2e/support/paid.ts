import { expect, type Page } from '@playwright/test';
import type { Audit } from './audit';
import { saveSecret, type Journey } from './state';
import { stripeStandIn } from './stack';

export type PaidJourney = Journey & { secondDatabaseId: string; secondDatabaseName: string; checkoutSessionId: string };

type StripeCall = { method: string; path: string; query: string[]; expand?: string[]; status: number };
export type StripeLog = {
  calls: StripeCall[];
  events: { type: string; object: string; status: number }[];
  cancels: { subscription: string; customer: string; organization: string }[];
  refunds: { payment: string; subscription: string; amount: number }[];
  unexpected: { method: string; host: string; path: string; query: string[]; reason: string }[];
};

const apiKeyFormat = /^(?:sk_)?[0-9A-HJKMNP-TV-Z]{26}_[0-9a-hjkmnp-tv-z]{52}$/;
const dashboardMetrics = 'http://127.0.0.1:8085/metrics';

export async function stripeLog(): Promise<StripeLog> {
  const response = await fetch(`${stripeStandIn}/log`);
  expect(response.status).toBe(200);
  return (await response.json()) as StripeLog;
}

export async function failingDeletionEntries(): Promise<number> {
  const response = await fetch(dashboardMetrics);
  expect(response.status).toBe(200);
  const value = /^terrascale_deletion_cleanup_failing_entries\{service="ts-dashboard"\} (\d+)$/m.exec(await response.text())?.[1];
  expect(value, 'the dashboard exports terrascale_deletion_cleanup_failing_entries').toBeDefined();
  return Number(value);
}

export async function createDatabase(page: Page, audit: Audit, name: string, region: string, label: string): Promise<string> {
  await page.goto('/databases');
  await page.getByRole('button', { name: 'New database', exact: true }).first().click();
  await expect(page.locator('#create-database[open]')).toBeVisible();
  await audit.check(`${label} dialog`);
  await page.locator('#database-name').fill(name);
  await page.locator('#create-database form').getByRole('button', { name: 'Create database', exact: true }).click();
  await page.waitForURL(/\/databases\/[0-9A-HJKMNP-TV-Z]{26}(\?|$)/);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(name);
  await expect(page.locator('main').getByText('ready', { exact: true }).first()).toBeVisible();
  await expect(page.locator('main').getByText(new RegExp(`^${region}$`, 'i')).first()).toBeVisible();
  await audit.check(label);
  return new URL(page.url()).pathname.split('/')[2];
}

export async function createApiKey(page: Page, audit: Audit, name: string, label: string): Promise<string> {
  await page.goto('/api-keys?create=key');
  const dialog = page.locator('#create-key[open]');
  await expect(dialog).toBeVisible();
  await page.locator('#key-name').fill(name);
  await dialog.getByRole('radio', { name: 'Full access', exact: true }).check();
  await audit.check(`${label} dialog`);
  await dialog.getByRole('button', { name: 'Create key', exact: true }).click();
  const secret = page.locator('#api-key-secret');
  await expect(page.locator('#key-secret')).toBeVisible();
  const apiKey = (await secret.textContent() ?? '').trim();
  saveSecret('paid-api-key', apiKey);
  expect(apiKeyFormat.test(apiKey), 'the API key secret has the canonical format').toBe(true);
  await audit.check(label);
  return apiKey;
}

export function databaseNames(page: Page) {
  return page.locator('main tbody a.ts-db-name .ts-strong');
}
