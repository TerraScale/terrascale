import assert from 'node:assert/strict';
import { mkdir, readFile } from 'node:fs/promises';
import { chromium } from 'playwright-core';

const base = process.argv[2];
assert(base, 'Pass the running C# host URL');
const browser = await chromium.launch({ headless: true });
const axe = await readFile(new URL('../../../node_modules/axe-core/axe.min.js', import.meta.url), 'utf8');
const screenshots = new URL('../../../test-results/dotnet/', import.meta.url);
await mkdir(screenshots, { recursive: true });
try {
  for (const [name, viewport] of [['desktop', { width: 1440, height: 1000 }], ['mobile', { width: 390, height: 844 }]]) {
    const context = await browser.newContext({ viewport });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    const response = await page.goto(`${base}/login`);
    assert.equal(response.status(), 200);
    await page.locator('#password').fill('browser-test-password');
    await page.getByRole('button', { name: 'Show password' }).click();
    assert.equal(await page.locator('#password').getAttribute('type'), 'text');
    await page.getByRole('button', { name: 'Hide password' }).click();
    assert.equal(await page.locator('#password').getAttribute('type'), 'password');
    await page.locator('#password').fill('');
    await page.evaluate(() => document.fonts.ready);
    await page.screenshot({ path: new URL(`login-${name}.png`, screenshots).pathname, fullPage: true });
    await page.evaluate(source => {
      const script = document.createElement('script');
      script.nonce = document.querySelector('script[nonce]').nonce;
      script.textContent = source;
      document.head.append(script);
    }, axe);
    const accessibility = await page.evaluate(() => axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'] } }));
    assert.deepEqual(accessibility.violations.map(value => ({ id: value.id, nodes: value.nodes.map(node => node.target) })), []);
    await page.getByRole('link', { name: 'Forgot password' }).click();
    await page.getByRole('heading', { name: 'Reset password' }).waitFor();
    await page.screenshot({ path: new URL(`reset-${name}.png`, screenshots).pathname, fullPage: true });
    await page.goto(`${base}/login/code`);
    await page.getByRole('heading', { name: 'Sign in with a code' }).waitFor();
    await page.goto(`${base}/missing`);
    await page.getByRole('heading', { name: 'We can’t find that page.' }).waitFor();
    await page.screenshot({ path: new URL(`notfound-${name}.png`, screenshots).pathname, fullPage: true });
    assert.deepEqual(errors, [], 'No browser script errors');
    await context.close();
  }
  console.log('PASS: desktop/mobile authentication rendering, Datastar password controls, WCAG 2.2 AA checks, navigation, illustrated 404');
} finally {
  await browser.close();
}
