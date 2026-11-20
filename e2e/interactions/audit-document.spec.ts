import { expect, test } from '@playwright/test';
import { Audit } from '../support/audit';
import { production } from '../support/stack';

test.use({ storageState: { cookies: [], origins: [] } });

test('a previous document cancel is not the next page failure, and the current page still reports its own', async ({ page }, info) => {
  test.skip(production, 'the audit fixture is exercised on the local stack');
  const audit = new Audit(page, info);
  let releaseSlow = () => {};
  const slow = new Promise<void>((resolve) => { releaseSlow = resolve; });
  await page.route('**/*', async (route) => {
    const url = route.request().url();
    if (url.endsWith('/first')) {
      await route.fulfill({ contentType: 'text/html', body: '<!doctype html><html lang="en"><head><title>first</title></head><body><main><a id="next" href="/second">next</a></main><script src="/slow.js"></script></body></html>' });
      return;
    }
    if (url.endsWith('/slow.js')) {
      await slow;
      await route.fulfill({ contentType: 'text/javascript', body: '' }).catch(() => undefined);
      return;
    }
    if (url.endsWith('/second')) {
      await route.fulfill({ contentType: 'text/html', body: '<!doctype html><html lang="en"><head><title>second</title></head><body><main><p>ready</p></main></body></html>' });
      return;
    }
    if (url.endsWith('/broken')) {
      await route.fulfill({ contentType: 'text/html', body: '<!doctype html><html lang="en"><head><title>broken</title></head><body><main><p>broken</p></main><script src="/broken.js"></script></body></html>' });
      return;
    }
    await route.abort('failed');
  });
  await page.goto('http://127.0.0.1:9/first', { waitUntil: 'commit' });
  await page.waitForRequest('**/slow.js');
  await page.click('#next');
  await page.getByText('ready').waitFor();
  releaseSlow();
  await audit.check('the next page');
  await page.goto('http://127.0.0.1:9/broken', { waitUntil: 'commit' });
  await expect(audit.check('the broken script')).rejects.toThrow(/broken\.js/);
});
