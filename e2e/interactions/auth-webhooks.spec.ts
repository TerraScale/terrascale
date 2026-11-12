import { randomBytes } from 'node:crypto';
import { Audit, auditedContext, expect, test } from '../support/audit';
import { confirmWithPassword, hostedReady, onboard, signUp, verify } from '../support/hosted';
import { newAddress, nextMail } from '../support/inbox';
import { enterSecret } from '../support/secret';
import { auth, dash, desktop, onLocalStack } from '../support/stack';
import { journey, saveCookies, saveSecret } from '../support/state';

type Delivery = { id: string; valid: boolean; code: number; event: { id: string; type: string; data: { email: string } } };
const receiver = 'http://127.0.0.1:8029';

async function received(after: number): Promise<Delivery[]> {
  const response = await fetch(`${receiver}/wait?after=${after}`, { signal: AbortSignal.timeout(20_000) });
  expect(response.status).toBe(200);
  return await response.json() as Delivery[];
}

test('auth webhooks verify signup signatures, retry failures and replay the original event', async ({ browser }, info) => {
  await onLocalStack(async () => {
    const owner = journey();
    const context = await auditedContext(browser, { viewport: desktop, storageState: { cookies: [], origins: [] } });
    const page = await context.newPage();
    const audit = new Audit(page, info);
    const email = await newAddress(`webhooks-${randomBytes(4).toString('hex')}`);
    const password = randomBytes(24).toString('base64url');
    saveSecret('webhook-owner-password', password);
    try {
      await signUp(page, audit, email, password, 'webhook owner');
      await verify(page, email);
      await onboard(page, audit, 'Webhook owner', `Webhooks ${owner.runId}`, 'webhook owner');
      await page.goto(`${dash}/auth/overview`);
      const projectId = new URL(await page.locator('#auth-sign-in-url').getAttribute('href') ?? '').pathname.split('/')[2];
      expect(projectId).toMatch(/^[0-9A-HJKMNP-TV-Z]{26}$/);
      await page.goto(`${dash}/auth/webhooks`);
      await page.getByRole('button', { name: 'Add webhook endpoint', exact: true }).first().click();
      await page.locator('#auth-webhook-url').fill('https://hooks.journey.terrascale.tech/receive');
      await page.locator('input[name=event_types][value="user.signed_up"]').check();
      await page.locator('#auth-create-webhook button[type=submit]').click();
      await confirmWithPassword(page, audit, password, 'webhook registration');
      await expect(page.locator('#auth-webhook-created-secret')).toBeVisible();
      const secret = await page.locator('#auth-webhook-created-secret').innerText();
      saveSecret('auth-webhook-signing-secret', secret);
      expect((await fetch(`${receiver}/configure`, { method: 'POST', body: JSON.stringify({ secret, failures: 1 }) })).status).toBe(200);
      await audit.check('registered authentication webhook');

      const userContext = await auditedContext(browser, { viewport: desktop, storageState: { cookies: [], origins: [] } });
      const userPage = await userContext.newPage();
      try {
        const userEmail = await newAddress(`webhook-user-${owner.runId}`);
        await userPage.goto(`${auth}/ui/${projectId}/sign-up`);
        await hostedReady(userPage);
        await userPage.locator('#sign-up-email').fill(userEmail);
        await enterSecret(userPage.locator('#sign-up-password'), password);
        await userPage.locator('form:has(#sign-up-email) button[type=submit]').click();
        const mail = await nextMail(userEmail, 0);
        const code = /^\s*(\d{6})\s*$/m.exec(mail.text)?.[1] ?? '';
        expect(code).toMatch(/^\d{6}$/);
        await enterSecret(userPage.locator('#sign-up-code-digit'), code);
        await userPage.getByRole('button', { name: 'Verify email' }).click();
        await expect(userPage.locator('[data-state=success]')).toContainText('signed in');
        const first = await received(0);
        expect(first[0]).toMatchObject({ valid: true, code: 500, event: { type: 'user.signed_up', data: { email: userEmail } } });
        const retried = await received(1);
        expect(retried[1]).toMatchObject({ id: first[0].id, valid: true, code: 200 });
        await expect.poll(async () => {
          await page.goto(`${dash}/auth/webhooks`);
          return page.locator('[data-webhook-delivery]').first().innerText();
        }).toContain('delivered');
        await audit.check('authentication delivery recorded');
        await page.getByRole('button', { name: 'Replay', exact: true }).first().click();
        const replay = await received(2);
        expect(replay[2]).toMatchObject({ id: first[0].id, valid: true, code: 200 });
        await page.goto(`${dash}/auth/webhooks`);
        await page.getByRole('button', { name: 'Send test event', exact: true }).click();
        const tested = await received(3);
        expect(tested[3]).toMatchObject({ valid: true, code: 200, event: { type: 'webhook.test' } });
        await page.goto(`${dash}/auth/logs`);
        await expect(page.locator('[data-auth-event]')).toHaveCount(4);
        await expect(page.locator('main')).toContainText('user.signed_up');
        await expect(page.locator('main')).toContainText('session.created');
        await audit.check('authentication events log');
        const firstEvent = await page.locator('[data-auth-event]').first().getAttribute('data-auth-event');
        await page.goto(`${dash}/auth/logs?after=${firstEvent}`);
        await expect(page.locator('[data-auth-event]')).toHaveCount(3);
        await expect(page.locator(`[data-auth-event="${firstEvent}"]`)).toHaveCount(0);
        await audit.check('authentication events after cursor');
        for (const url of ['https://hooks.journey.terrascale.tech/redirect', 'https://127.0.0.1:8029/receive']) {
          await page.goto(`${dash}/auth/webhooks`);
          await page.getByRole('button', { name: 'Add webhook endpoint', exact: true }).first().click();
          await page.locator('#auth-webhook-url').fill(url);
          await page.locator('input[name=event_types][value="user.signed_up"]').check();
          await page.locator('#auth-create-webhook button[type=submit]').click();
      await confirmWithPassword(page, audit, password, 'guarded webhook registration');
          const signingSecret = page.locator('#auth-webhook-created-secret');
          await expect(signingSecret).toBeVisible();
          saveSecret('guarded-webhook-secret', await signingSecret.innerText());
          await page.goto(`${dash}/auth/webhooks`);
          await page.locator('tr').filter({ hasText: url }).getByRole('button', { name: 'Send test event', exact: true }).click();
          await expect.poll(async () => {
            await page.goto(`${dash}/auth/webhooks`);
            return page.getByRole('region', { name: `Deliveries to ${url}`, exact: true }).innerText();
          }).toContain(url.includes('127.0.0.1') ? 'No response' : '302');
          await audit.check('guarded authentication delivery');
        }
        const allReceived = await (await fetch(`${receiver}/deliveries`)).json();
        expect(allReceived).toHaveLength(4);

      } finally {
        await saveCookies(userContext);
        await userContext.close();
      }
      await page.goto(`${dash}/profile?tab=danger`);
      await page.locator('main').getByRole('button', { name: 'Delete account', exact: true }).click();
      await page.locator('#delete-account-email').fill(email);
      await page.locator('#delete-account').getByRole('button', { name: 'Delete account', exact: true }).click();
      await confirmWithPassword(page, audit, password, 'webhook owner cleanup');
      await page.waitForURL((url) => url.origin === dash && url.pathname === '/account-deleted');
      await expect(page.getByRole('heading', { level: 1 })).toHaveText('Account deleted');
      await audit.check('webhook owner deleted');
      await audit.finish();
    } finally {
      await saveCookies(context);
      await context.close();
    }
  });
});
