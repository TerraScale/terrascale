import { randomBytes } from 'node:crypto';
import type { Page } from '@playwright/test';
import { Audit, auditedContext, expect, test } from '../support/audit';
import { confirmWithPassword, hostedReady, onboard, signUp, verify } from '../support/hosted';
import { newAddress, nextMail } from '../support/inbox';
import { enterSecret } from '../support/secret';
import { auth, dash, desktop, onLocalStack } from '../support/stack';
import { journey, saveCookies, saveSecret } from '../support/state';

type Delivery = { receivedAt: number; id: string; valid: boolean; code: number; event: { id: string; type: string; data: { email: string } } };
const receiver = 'http://127.0.0.1:8029';
test.describe.configure({ mode: 'default' });

async function received(after: number): Promise<Delivery[]> {
  const response = await fetch(`${receiver}/wait?after=${after}`, { signal: AbortSignal.timeout(20_000) });
  expect(response.status).toBe(200);
  return await response.json() as Delivery[];
}

test('auth webhooks verify invited signup signatures, retry failures and replay the original event', async ({ browser }, info) => {
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
        const userEmail = await newAddress(`webhook-user-${randomBytes(4).toString('hex')}`);
        await page.goto(dash + '/auth/restrictions');
        await page.locator('#signup-mode').selectOption('restricted');
        await page.getByRole('button', { name: 'Save restrictions' }).click();
        await expect(page.locator('#signup-mode')).toHaveValue('restricted');
        await page.goto(dash + '/auth/invitations');
        await page.locator('#invitation-email').fill(userEmail);
        await page.getByRole('button', { name: 'Invite user' }).click();
        await expect(page.locator('.ts-list-item').filter({ hasText: userEmail })).toContainText('pending');
        const invitation = await nextMail(userEmail, 0);
        const link = invitation.text.match(new RegExp(auth + '/ui/' + projectId + '/invitations/[A-Za-z0-9_-]{43}'))?.[0];
        expect(link).toBeTruthy();
        saveSecret('webhook-invitation-token', new URL(link!).pathname.split('/').at(-1)!);
        await userPage.goto(link!);
        await hostedReady(userPage);
        await expect(userPage.locator('#sign-up-email')).toHaveValue(userEmail);
        await enterSecret(userPage.locator('#sign-up-password'), password);
        await userPage.locator('form:has(#sign-up-email) button[type=submit]').click();
        const mail = await nextMail(userEmail, 1);
        const code = /^\s*(\d{6})\s*$/m.exec(mail.text)?.[1] ?? '';
        expect(code).toMatch(/^\d{6}$/);
        await enterSecret(userPage.locator('#sign-up-code-digit'), code);
        await userPage.getByRole('button', { name: 'Verify email' }).click();
        await expect(userPage.locator('[data-state=success]')).toContainText('signed in');
        const first = await received(0);
        expect(first[0]).toMatchObject({ valid: true, code: 500, event: { type: 'user.signed_up', data: { email: userEmail } } });
        const retried = await received(1);
        expect(retried[1]).toMatchObject({ id: first[0].id, valid: true, code: 200 });
        expect(retried[1].receivedAt - first[0].receivedAt).toBeGreaterThanOrEqual(2500);
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
        await expect.poll(async () => {
          await page.goto(`${dash}/auth/logs`);
          return page.locator('[data-auth-event]').count();
        }).toBe(4);
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

test('auth webhook enqueue of 200 in one project allows another project through and drains in order', async ({ browser }, info) => {
  await onLocalStack(async () => {
    const owner = journey();
    const context = await auditedContext(browser, { viewport: desktop, storageState: { cookies: [], origins: [] } });
    const other = await auditedContext(browser, { viewport: desktop, storageState: { cookies: [], origins: [] } });
    const page = await context.newPage();
    const otherPage = await other.newPage();
    const audit = new Audit(page, info);
    const otherAudit = new Audit(otherPage, info);
    const password = randomBytes(24).toString('base64url');
    const email = await newAddress(`webhook-fairness-${randomBytes(4).toString('hex')}`);
    saveSecret('fairness-owner-password', password);
    const otherPassword = randomBytes(24).toString('base64url');
    const otherEmail = await newAddress(`webhook-healthy-${randomBytes(4).toString('hex')}`);
    saveSecret('fairness-healthy-owner-password', otherPassword);
    const pathA = `/receive/a-${randomBytes(4).toString('hex')}`;
    const pathB = `/receive/b-${randomBytes(4).toString('hex')}`;
    const queued: string[] = [];
    const register = async (target: typeof page, targetAudit: Audit, path: string, credential: string) => {
      await target.goto(`${dash}/auth/webhooks`);
      await target.getByRole('button', { name: 'Add webhook endpoint', exact: true }).first().click();
      await target.locator('#auth-webhook-url').fill(`https://hooks.journey.terrascale.tech${path}`);
      await target.locator('input[name=event_types][value="user.signed_up"]').check();
      await target.locator('#auth-create-webhook button[type=submit]').click();
      await confirmWithPassword(target, targetAudit, credential, 'fairness webhook registration');
      const secret = await target.locator('#auth-webhook-created-secret').innerText();
      saveSecret('fairness-signing-secret', secret);
      expect((await fetch(`${receiver}/configure`, { method: 'POST', body: JSON.stringify({ path, secret, hold: path === pathA }) })).status).toBe(200);
      await target.goto(`${dash}/auth/webhooks`);
      const form = target.locator('tr').filter({ hasText: path }).locator('form').filter({ has: target.getByRole('button', { name: 'Send test event', exact: true }) });
      const action = await form.getAttribute('action');
      const fields = await form.locator('input[type=hidden]').evaluateAll(inputs => Object.fromEntries(inputs.map(input => [(input as HTMLInputElement).name, (input as HTMLInputElement).value])));
      expect(action).toBeTruthy();
      return async () => {
        const response = await target.request.post(new URL(action!, dash).href, { form: fields });
        expect(response.status()).toBe(200);
      };
    };
    try {
      await signUp(page, audit, email, password, 'fairness webhook owner');
      await verify(page, email);
      await onboard(page, audit, 'Fairness owner', `Fairness ${owner.runId}`, 'fairness webhook owner');
      const enqueueA = await register(page, audit, pathA, password);
      await signUp(otherPage, otherAudit, otherEmail, otherPassword, 'fairness healthy owner');
      await verify(otherPage, otherEmail);
      await onboard(otherPage, otherAudit, 'Healthy owner', `Healthy ${owner.runId}`, 'fairness healthy owner');
      const enqueueB = await register(otherPage, otherAudit, pathB, otherPassword);
      await enqueueA();
      await expect.poll(async () => {
        const rows = await (await fetch(`${receiver}/deliveries`)).json() as (Delivery & { path: string })[];
        return rows.filter(row => row.path === pathA).length;
      }).toBe(1);
      await Promise.all([
        (async () => {
          for (let offset = 1; offset < 200; offset += 8) {
            await Promise.all(Array.from({ length: Math.min(8, 200 - offset) }, () => enqueueA()));
          }
        })(),
        (async () => {
          await enqueueB();
          await expect.poll(async () => {
            const rows = await (await fetch(`${receiver}/deliveries`)).json() as (Delivery & { path: string })[];
            return rows.filter(row => row.path === pathB).length;
          }).toBe(1);
          const before = await (await fetch(`${receiver}/deliveries`)).json() as (Delivery & { path: string })[];
          expect(before.filter(row => row.path === pathA)).toHaveLength(1);
          expect(before.filter(row => row.path === pathB)[0]).toMatchObject({ valid: true, code: 200 });
          expect((await fetch(`${receiver}/release`, { method: 'POST', body: JSON.stringify({ path: pathA }) })).status).toBe(200);
        })(),
      ]);
      await expect.poll(async () => {
        const rows = await (await fetch(`${receiver}/deliveries`)).json() as (Delivery & { path: string })[];
        return rows.filter(row => row.path === pathA).length;
      }).toBe(200);
      const drained = (await (await fetch(`${receiver}/deliveries`)).json() as (Delivery & { path: string })[]).filter(row => row.path === pathA);
      queued.push(...drained.map(row => row.id));
      expect(new Set(queued).size).toBe(200);
      expect(queued).toEqual([...queued].sort());
      expect(drained.every(row => row.valid && row.code === 200)).toBe(true);
      console.log('Webhook fairness: project B delivered with only one A request in flight; all 200 A deliveries drained in order.');
      await otherPage.goto(`${dash}/auth/webhooks`);
      await otherPage.locator('tr').filter({ hasText: pathB }).getByRole('button', { name: 'Delete', exact: true }).click();
      for (const account of [
        { page, audit, email, password },
        { page: otherPage, audit: otherAudit, email: otherEmail, password: otherPassword },
      ]) {
        await account.page.goto(`${dash}/profile?tab=danger`);
        await account.page.locator('main').getByRole('button', { name: 'Delete account', exact: true }).click();
        await account.page.locator('#delete-account-email').fill(account.email);
        await account.page.locator('#delete-account').getByRole('button', { name: 'Delete account', exact: true }).click();
        await confirmWithPassword(account.page, account.audit, account.password, 'fairness webhook owner cleanup');
        await account.page.waitForURL(url => url.origin === dash && url.pathname === '/account-deleted');
        await expect(account.page.getByRole('heading', { level: 1 })).toHaveText('Account deleted');
        await account.audit.finish();
      }
    } finally {
      await saveCookies(context);
      await saveCookies(other);
      await context.close();
      await other.close();
    }
  });
});

test('auth webhook timeouts release delivery lanes for another project within one endpoint pass', async ({ browser }, info) => {
  await onLocalStack(async () => {
    const owner = journey();
    const contexts = await Promise.all([0, 1, 2].map(() => auditedContext(browser, { viewport: desktop, storageState: { cookies: [], origins: [] } })));
    const accounts = await Promise.all(contexts.map(async (context, index) => {
      const page = await context.newPage();
      const audit = new Audit(page, info);
      const email = await newAddress('webhook-timeout-' + randomBytes(4).toString('hex'));
      const password = randomBytes(24).toString('base64url');
      saveSecret('timeout-owner-password', password);
      await signUp(page, audit, email, password, 'timeout webhook owner');
      await verify(page, email);
      await onboard(page, audit, 'Timeout owner', 'Timeout ' + owner.runId + '-' + index, 'timeout webhook owner');
      return { page, audit, email, password };
    }));
    const register = async (page: Page, audit: Audit, password: string, hold: boolean) => {
      const path = '/receive/timeout-' + randomBytes(8).toString('hex');
      await page.goto(dash + '/auth/webhooks');
      await page.getByRole('button', { name: 'Add webhook endpoint', exact: true }).first().click();
      await page.locator('#auth-webhook-url').fill('https://hooks.journey.terrascale.tech' + path);
      await page.locator('input[name=event_types][value="user.signed_up"]').check();
      await page.locator('#auth-create-webhook button[type=submit]').click();
      await confirmWithPassword(page, audit, password, 'timeout webhook registration');
      const secret = await page.locator('#auth-webhook-created-secret').innerText();
      saveSecret('timeout-signing-secret', secret);
      expect((await fetch(receiver + '/configure', { method: 'POST', body: JSON.stringify({ path, secret, hold }) })).status).toBe(200);
      await page.goto(dash + '/auth/webhooks');
      const row = page.locator('tr').filter({ hasText: path });
      const form = row.locator('form').filter({ has: page.getByRole('button', { name: 'Send test event', exact: true }) });
      const action = await form.getAttribute('action');
      const fields = await form.locator('input[type=hidden]').evaluateAll(inputs => Object.fromEntries(inputs.map(input => [(input as HTMLInputElement).name, (input as HTMLInputElement).value])));
      expect(action).toBeTruthy();
      return { path, enqueue: async () => {
        const response = await page.request.post(new URL(action!, dash).href, { form: fields });
        expect(response.status()).toBe(200);
      } };
    };
    try {
      const failing = (await Promise.all(accounts.slice(0, 2).map(async (account, index) => {
        const endpoints = [];
        for (let count = 0; count < (index === 0 ? 3 : 2); count++) {
          endpoints.push(await register(account.page, account.audit, account.password, true));
        }
        return endpoints;
      }))).flat();
      const healthyOwner = accounts[2];
      const healthy = await register(healthyOwner.page, healthyOwner.audit, healthyOwner.password, false);
      const saturatingAt = performance.now();
      await Promise.all(failing.slice(0, 4).map(endpoint => endpoint.enqueue()));
      await expect.poll(async () => {
        const rows = await (await fetch(receiver + '/deliveries')).json() as (Delivery & { path: string })[];
        return new Set(rows.filter(row => failing.some(endpoint => endpoint.path === row.path)).map(row => row.path)).size;
      }).toBe(4);
      expect(performance.now() - saturatingAt).toBeLessThan(20_000);
      const queuedAt = performance.now();
      const deadline = AbortSignal.timeout(30_000);
      await Promise.all([
        healthy.enqueue(),
        ...failing.map(async (endpoint, index) => {
          for (let count = index < 4 ? 1 : 0; count < 10; count++) await endpoint.enqueue();
        }),
      ]);
      let rows = await (await fetch(receiver + '/deliveries')).json() as (Delivery & { path: string })[];
      while (!rows.some(row => row.path === healthy.path)) {
        const response = await fetch(receiver + '/wait?after=' + rows.length, { signal: deadline });
        expect(response.status).toBe(200);
        rows = await response.json() as (Delivery & { path: string })[];
      }
      const delivered = rows.find(row => row.path === healthy.path);
      expect(delivered).toMatchObject({ valid: true, code: 200, event: { type: 'webhook.test' } });
      expect(performance.now() - queuedAt).toBeLessThan(30_000);
      expect(rows.filter(row => failing.some(endpoint => endpoint.path === row.path)).length).toBeLessThanOrEqual(5);
      console.log('Webhook timeouts: four occupied lanes, five failing endpoints with ten deliveries each; healthy project delivered within one 30-second endpoint pass.');
      await healthyOwner.page.goto(dash + '/auth/webhooks');
      await healthyOwner.page.locator('tr').filter({ hasText: healthy.path }).getByRole('button', { name: 'Delete', exact: true }).click();
      await Promise.all(accounts.map(async ({ page, audit, email, password }) => {
        await page.goto(dash + '/profile?tab=danger');
        await page.locator('main').getByRole('button', { name: 'Delete account', exact: true }).click();
        await page.locator('#delete-account-email').fill(email);
        await page.locator('#delete-account').getByRole('button', { name: 'Delete account', exact: true }).click();
        await confirmWithPassword(page, audit, password, 'timeout webhook owner cleanup');
        await page.waitForURL(url => url.origin === dash && url.pathname === '/account-deleted');
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('Account deleted');
        await audit.finish();
      }));
    } finally {
      for (const context of contexts) {
        await saveCookies(context);
        await context.close();
      }
    }
  });
});
