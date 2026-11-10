import { test, expect } from '../support/audit';
import { shell } from '../support/shell';
import { dash } from '../support/stack';
import { journey } from '../support/state';
import { dashboardStates } from './inventory';

const literal = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

for (const state of dashboardStates) {
  test(state.name, async ({ page, audit }) => {
    const current = journey();
    const target = new URL(state.url(current), dash);
    if (state.status) audit.expectStatus(new RegExp(`^${literal(target.origin + target.pathname)}(\\?|$)`), state.status);
    const response = await page.goto(target.href);
    expect(response?.status(), `HTTP status of ${state.name}`).toBe(state.status ?? 200);
    if (state.status === undefined) await shell(page, current);
    await state.data?.(page, current);
    await audit.check(state.name);
  });
}
