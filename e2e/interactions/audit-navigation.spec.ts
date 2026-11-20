import { expect, test, type Page, type Route } from '@playwright/test';
import { Audit } from '../support/audit';

const site = 'http://127.0.0.1:9';
const html = (body: string) => `<!doctype html><html lang="en"><head><title>Audit navigation</title></head><body><main>${body}</main></body></html>`;

async function holdRequest(page: Page, path: string): Promise<{ held: Promise<Route> }> {
  let hold!: (route: Route) => void;
  const held = new Promise<Route>((resolve) => { hold = resolve; });
  await page.route(site + path, hold);
  return { held };
}

test('two auditors exclude a canceled request owned by the replaced page', async ({ page }, info) => {
  await page.route(site + '/first', (route) => route.fulfill({ contentType: 'text/html', body: html('<script defer src="/slow.js"></script>') }));
  await page.route(site + '/second', (route) => route.fulfill({ contentType: 'text/html', body: html('<p>Second page</p>') }));
  const { held } = await holdRequest(page, '/slow.js');
  const audits = [new Audit(page, info), new Audit(page, info)];
  await page.goto(site + '/first', { waitUntil: 'commit' });
  const route = await held;
  const request = route.request();
  const failed = page.waitForEvent('requestfailed', (candidate) => candidate === request);
  await route.abort('aborted');
  expect((await failed).failure()?.errorText).toBe('net::ERR_ABORTED');
  await page.goto(site + '/second');
  await expect(page.getByText('Second page')).toBeVisible();
  for (const audit of audits) await audit.finish();
});

test('both auditors report the same request canceled after anchor and history transitions', async ({ page }, info) => {
  await page.route(site + '/stay', (route) => route.fulfill({
    contentType: 'text/html', body: html('<a href="#section">Jump</a><p id="section">Current page</p>'),
  }));
  const { held } = await holdRequest(page, '/cancel.js');
  await page.goto(site + '/stay');
  const audits = [new Audit(page, info), new Audit(page, info)];
  await page.evaluate(() => {
    const script = document.createElement('script');
    script.src = '/cancel.js';
    document.body.append(script);
  });
  const route = await held;
  const request = route.request();
  const failed = page.waitForEvent('requestfailed', (candidate) => candidate === request);
  await page.getByRole('link', { name: 'Jump' }).click();
  await expect(page).toHaveURL(site + '/stay#section');
  await page.evaluate(() => history.pushState({}, '', '/stay?history=1#section'));
  await expect(page).toHaveURL(site + '/stay?history=1#section');
  expect(request.failure()).toBeNull();
  await route.abort('aborted');
  expect((await failed).failure()?.errorText).toBe('net::ERR_ABORTED');

  const line = `request failed: GET ${site}/cancel.js net::ERR_ABORTED`;
  for (const audit of audits) await expect(audit.finish()).rejects.toThrow(line);
});
