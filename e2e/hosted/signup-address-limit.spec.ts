import { randomBytes } from 'node:crypto';
import { test, expect } from '../support/audit';
import { budgetWindow, waitForBudgetWindow } from '../support/budget-window';
import { clientProxy } from '../support/client-address';
import { signupCount } from '../support/signup-counts';
import { auth, production } from '../support/stack';
import { saveSecret } from '../support/state';

test('one client address admits twenty platform sign-ups and refuses forged forwarding on the next', async ({ request }) => {
  test.skip(production, 'Requires namespace-local source addresses and stored limiter counts.');
  const password = randomBytes(24).toString('base64url');
  const run = randomBytes(8).toString('hex');
  saveSecret('signup-address-password', password);
  const address = new URL(clientProxy()!.server).hostname;
  expect(await signupCount(address)).toBe(0);
  const window = 60 * 60_000;
  await waitForBudgetWindow(request, window);
  let firstWindow: number | undefined;
  for (let index = 0; index < 21; index++) {
    const response = await request.post(`${auth}/ui/terrascale/sign-up`, {
      headers: { Origin: auth, 'X-Forwarded-For': `198.51.100.${index + 1}`, 'X-Real-IP': `192.0.2.${index + 1}` },
      form: { email: `signup-limit-${run}-${index}@example.com`, password },
    });
    firstWindow ??= budgetWindow(response, window);
    expect(budgetWindow(response, window)).toBe(firstWindow);
    expect(response.status()).toBe(index < 20 ? 200 : 429);
    const body = await response.text();
    if (index < 20) {
      expect(body).toContain('pending_token');
      const pending = /name="pending_token" value="([^"]+)"/.exec(body)?.[1];
      expect(pending).toBeTruthy();
      saveSecret('signup-address-pending', pending!);
    } else {
      expect(response.headers()['retry-after']).toBeTruthy();
    }
  }
  expect(await signupCount(address)).toBe(20);
  console.log(`signup limit: ${address} admitted 20/20; attempt 21 refused 429 despite forged forwarding; stored 20/20`);
});
