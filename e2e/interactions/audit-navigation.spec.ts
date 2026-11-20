import { expect, test } from '@playwright/test';
import { Audit } from '../support/audit';
import { production } from '../support/stack';

const pageHtml = (title: string, body: string) =>
  `<!doctype html><html lang="en"><head><title>${title}</title></head><body><main>${body}</main></body></html>`;

async function abortedLine(check: Promise<void>): Promise<string> {
  try {
    await check;
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    const line = message.split('\n').map((item) => item.trim()).find((item) => item.includes('net::ERR_ABORTED'));
    if (!line) throw error;
    return line;
  }
  throw new Error('the canceled request was not reported');
}

test('a full navigation drops the previous page cancel, and an anchor or history change still reports the current one', async ({ page }, info) => {
  test.skip(production, 'the audit fixture is exercised on the local stack');
  let releaseSlow = () => {};
  const slow = new Promise<void>((resolve) => { releaseSlow = resolve; });
  await page.route('**/*', async (route) => {
    const url = route.request().url();
    if (url.endsWith('/first')) {
      await route.fulfill({ contentType: 'text/html', body: pageHtml('first', '<a id="next" href="/second">next</a>') + '<script src="/slow.js"></script>' });
      return;
    }
    if (url.endsWith('/slow.js')) {
      await slow;
      await route.fulfill({ contentType: 'text/javascript', body: '' }).catch(() => undefined);
      return;
    }
    if (url.endsWith('/second') || url.endsWith('/stay') || url.endsWith('/control')) {
      const name = url.endsWith('/control') ? 'control' : 'stay';
      const extra = url.endsWith('/control') ? '<script src="/cancel.js"></script>' : '';
      await route.fulfill({ contentType: 'text/html', body: pageHtml(name, `<p>${name}</p><a id="here" href="#section">here</a>${extra}`) });
      return;
    }
    if (url.endsWith('/cancel.js')) {
      await route.abort('aborted');
      return;
    }
    await route.abort('failed');
  });
  const crossed = new Audit(page, info);
  const failed = page.waitForEvent('requestfailed', { predicate: (request) => request.url().endsWith('/slow.js') });
  await page.goto('http://127.0.0.1:9/first', { waitUntil: 'commit' });
  await page.waitForRequest('**/slow.js');
  await page.click('#next');
  await failed;
  await page.getByText('stay').waitFor().catch(() => page.getByText('second').waitFor());
  releaseSlow();
  await crossed.check('the next page');

  const control = new Audit(page, info);
  const controlWait = page.waitForEvent('requestfailed', { predicate: (request) => request.url().endsWith('/cancel.js') });
  await page.goto('http://127.0.0.1:9/control', { waitUntil: 'commit' });
  await controlWait;
  const controlLine = await abortedLine(control.check('the control page'));

  const current = new Audit(page, info);
  await page.goto('http://127.0.0.1:9/stay', { waitUntil: 'commit' });
  const anchor = page.waitForEvent('framenavigated');
  await page.click('#here');
  await anchor;
  await page.evaluate(() => history.pushState({}, '', '/stay?kept=1'));
  const currentWait = page.waitForEvent('requestfailed', { predicate: (request) => request.url().endsWith('/cancel.js') });
  await page.addScriptTag({ url: '/cancel.js' }).catch(() => undefined);
  await currentWait;
  expect(await abortedLine(current.check('the current page'))).toBe(controlLine);
});
