import { test, expect } from '../support/audit';
import { shell } from '../support/shell';
import { dash } from '../support/stack';
import { journey } from '../support/state';
import { dashboardStates } from './inventory';

const literal = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

for (const state of dashboardStates) {
  test.describe(() => {
    if (state.signedOut) test.use({ storageState: { cookies: [], origins: [] } });
    test(state.name, async ({ page, audit }) => {
      const current = journey();
      const target = new URL(await state.url(current, page), dash);
      if (state.status) audit.expectStatus(new RegExp(`^${literal(target.origin + target.pathname)}(\\?|$)`), state.status);
      const response = await page.goto(target.href);
      expect(response?.status(), `${state.name}: HTTP status`).toBe(state.status ?? 200);
      const reached = new URL(page.url());
      expect(reached.pathname, `${state.name}: the page reached`).toBe(state.path ?? target.pathname);
      for (const [key, value] of target.searchParams) expect(reached.searchParams.get(key), `${state.name}: query parameter ${key}`).toBe(value);
      await expect(state.heading(page, current), `${state.name}: the heading only this page or tab has`).toBeVisible();
      if (state.shell !== false) await shell(page, current);
      await state.data?.(page, current);
      await audit.check(state.name);
    });
  });
}
