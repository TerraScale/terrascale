import type { APIRequestContext, APIResponse } from '@playwright/test';
import { expect } from './audit';
import { auth } from './stack';

export function budgetWindow(response: APIResponse, window: number): number {
  const date = Date.parse(response.headers().date);
  expect(Number.isFinite(date), 'budget response has a server Date').toBe(true);
  return Math.floor(date / window);
}

export async function waitForBudgetWindow(request: APIRequestContext, window: number): Promise<void> {
  await expect.poll(async () => {
    const ready = await request.get(auth + '/readyz');
    expect(ready.status()).toBe(200);
    const date = Date.parse(ready.headers().date);
    expect(Number.isFinite(date), 'budget readiness has a server Date').toBe(true);
    return date % window;
  }, { message: 'budget probe starts with at least fifteen seconds left in its fixed window' }).toBeLessThan(window - 15_000);
}
