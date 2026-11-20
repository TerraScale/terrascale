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

async function startScript(page: Page, path: string): Promise<void> {
  await page.evaluate((src) => {
    const script = document.createElement('script');
    script.src = src;
    document.body.append(script);
  }, path);
}

function gate(): { wait: Promise<void>; release(): void } {
  let release!: () => void;
  const wait = new Promise<void>((resolve) => { release = resolve; });
  return { wait, release };
}

test('both auditors retain an abort observed before an unrelated navigation', async ({ page }, info) => {
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
  const line = `request failed: GET ${site}/slow.js net::ERR_ABORTED`;
  for (const audit of audits) await expect(audit.finish()).rejects.toThrow(line);
});

test('both auditors exclude a held request canceled by page replacement', async ({ page }, info) => {
  await page.route(site + '/first', (route) => route.fulfill({ contentType: 'text/html', body: html('<p>First page</p>') }));
  const script = await holdRequest(page, '/slow.js');
  const navigation = await holdRequest(page, '/second');
  const audits = [new Audit(page, info), new Audit(page, info)];
  await page.goto(site + '/first');
  await startScript(page, '/slow.js');
  const scriptRoute = await script.held;
  const request = scriptRoute.request();
  const failed = page.waitForEvent('requestfailed', (candidate) => candidate === request);

  const replacementStarted = page.waitForRequest((candidate) => candidate.isNavigationRequest() && candidate.url() === site + '/second');
  const replaced = page.goto(site + '/second');
  const navigationRoute = await navigation.held;
  expect((await replacementStarted).url()).toBe(navigationRoute.request().url());
  expect(page.url()).toBe(site + '/first');
  expect(request.failure()).toBeNull();
  await scriptRoute.abort('aborted');
  expect((await failed).failure()?.errorText).toBe('net::ERR_ABORTED');
  await navigationRoute.fulfill({ contentType: 'text/html', body: html('<p>Second page</p>') });
  await replaced;
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
  await startScript(page, '/cancel.js');
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

test('finish includes a failure delivered while an earlier abort is classified', async ({ page }, info) => {
  await page.route(site + '/first', (route) => route.fulfill({ contentType: 'text/html', body: html('<p>First page</p>') }));
  const prior = await holdRequest(page, '/prior.js');
  const navigation = await holdRequest(page, '/second');
  const active = await holdRequest(page, '/active.js');
  await page.goto(site + '/first');

  const frame = page.mainFrame();
  const evaluate = frame.evaluate;
  const runEvaluate = evaluate.bind(frame);
  const ownerReady = gate();
  const releaseOwner = gate();
  let first = true;
  frame.evaluate = ((...args: Parameters<typeof frame.evaluate>) => {
    const result = runEvaluate(...args);
    if (!first) return result;
    first = false;
    return result.then(async (identity) => {
      ownerReady.release();
      await releaseOwner.wait;
      return identity;
    });
  }) as typeof frame.evaluate;

  try {
    const audit = new Audit(page, info);
    await ownerReady.wait;
    await startScript(page, '/prior.js');
    const priorRoute = await prior.held;
    const priorFailure = page.waitForEvent('requestfailed', (request) => request === priorRoute.request());
    const navigating = page.goto(site + '/second');
    const navigationRoute = await navigation.held;
    await priorRoute.abort('aborted');
    expect((await priorFailure).failure()?.errorText).toBe('net::ERR_ABORTED');
    await navigationRoute.fulfill({ contentType: 'text/html', body: html('<p>Second page</p>') });
    await navigating;

    const draining = gate();
    const collector = audit as unknown as { failures(): Promise<string[]> };
    const collect = collector.failures.bind(audit);
    collector.failures = () => {
      const result = collect();
      draining.release();
      return result;
    };
    const finishing = audit.finish().then(() => '<passed>', (error: Error) => error.message);
    await draining.wait;
    await startScript(page, '/active.js');
    const activeRoute = await active.held;
    const activeFailure = page.waitForEvent('requestfailed', (request) => request === activeRoute.request());
    await activeRoute.abort('failed');
    expect((await activeFailure).failure()?.errorText).toBe('net::ERR_FAILED');
    releaseOwner.release();

    const result = await finishing;
    expect(result).toContain(`request failed: GET ${site}/active.js net::ERR_FAILED`);
    expect(result).not.toContain('prior.js net::ERR_ABORTED');
  } finally {
    releaseOwner.release();
    frame.evaluate = evaluate;
  }
});
