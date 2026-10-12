import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { chromium } from 'playwright-core';

const base = process.argv[2];
assert(base, 'Pass the running test-only C# demo URL');
const browser = await chromium.launch({ headless: true });
const axe = await readFile(new URL('../../node_modules/axe-core/axe.min.js', import.meta.url), 'utf8');
const errors = [];
try {
  for (const viewport of [{ width: 1440, height: 900 }, { width: 390, height: 844 }]) {
    const context = await browser.newContext({ viewport });
    const page = await context.newPage();
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(base + '/login/code?returnTo=%2F__test%2Fsession');
    await page.locator('#email').fill('user@example.com');
    await page.getByRole('button', { name: 'Email me a code', exact: true }).click();
    await page.locator('.ts-email-code-ready').waitFor();
    const code = page.getByRole('textbox', { name: 'Six-digit code', exact: true });
    await code.pressSequentially('1x2-34567');
    assert.equal(await code.inputValue(), '123456', 'typing rejects non-digits and overflow');
    assert.deepEqual(await page.locator('.ts-email-code-slot').allTextContents(), ['1', '2', '3', '4', '5', '6']);
    await code.press('ControlOrMeta+ArrowLeft');
    await code.press('ControlOrMeta+Shift+ArrowRight');
    await code.pressSequentially('654321');
    assert.equal(await code.inputValue(), '654321', 'selection replacement keeps six digits');
    await code.press('ControlOrMeta+z');
    assert.notEqual(await code.inputValue(), '654321', 'native editing keeps undo history');
    await code.fill('');
    await code.evaluate(el => {
      const clipboardData = new DataTransfer();
      clipboardData.setData('text/plain', '12 34-56 extra 7');
      el.dispatchEvent(new ClipboardEvent('paste', { clipboardData, bubbles: true, cancelable: true }));
    });
    assert.equal(await code.inputValue(), '123456', 'formatted paste normalizes the six digits');
    await code.fill('000000');
    const [rejected] = await Promise.all([
      page.waitForResponse(response => response.url().endsWith('/login/code/verify') && response.request().method() === 'POST'),
      page.getByRole('button', { name: 'Continue', exact: true }).click()
    ]);
    assert.equal(rejected.status(), 401);
    await page.locator('#code-error').waitFor();
    assert.equal(await code.getAttribute('aria-invalid'), 'true');
    await page.getByRole('button', { name: 'Send a new code', exact: true }).click();
    await page.locator('.ts-email-code-ready').waitFor();
    await page.evaluate(source => {
      const script = document.createElement('script'); script.nonce = document.querySelector('script[nonce]').nonce;
      script.textContent = source; document.head.append(script);
    }, axe);
    const violations = await page.evaluate(() => axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'] } }));
    assert.deepEqual(violations.violations.map(value => value.id), [], 'email-code screen is accessible');
    await code.fill('123456');
    const [signedIn] = await Promise.all([
      page.waitForResponse(response => response.url().endsWith('/login/code/verify') && response.request().method() === 'POST'),
      page.getByRole('button', { name: 'Continue', exact: true }).click()
    ]);
    assert.equal(signedIn.status(), 303);
    await page.waitForURL(base + '/__test/session');
    assert.equal((await page.request.get(base + '/__test/session')).status(), 200);
    await context.close();
  }
  // Native forms remain usable when Datastar has not loaded or JS is disabled.
  const native = await browser.newContext({ javaScriptEnabled: false });
  const page = await native.newPage();
  await page.goto(base + '/login?returnTo=%2F__test%2Fsession');
  await page.locator('#email').fill('user@example.com');
  await page.locator('#password').fill('test-password');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.waitForURL(base + '/__test/session');
  assert.equal((await page.request.get(base + '/__test/session')).status(), 200);
  await native.close();
  const context = await browser.newContext();
  const reset = await context.newPage();
  await reset.goto(base + '/reset-password');
  await reset.locator('#email').fill('user@example.com');
  await reset.getByRole('button', { name: 'Email me a code', exact: true }).click();
  await reset.locator('#confirmPassword').waitFor();
  await reset.locator('#code').fill('123456');
  await reset.locator('#password').fill('new-test-password');
  await reset.locator('#confirmPassword').fill('mismatching-password');
  await reset.getByRole('button', { name: 'Change password', exact: true }).click();
  await reset.locator('#confirmPassword-error').waitFor();
  assert.equal(await reset.locator('#confirmPassword').getAttribute('aria-invalid'), 'true');
  await reset.locator('#code').fill('123456');
  await reset.locator('#password').fill('new-test-password');
  await reset.locator('#confirmPassword').fill('new-test-password');
  await reset.getByRole('button', { name: 'Change password', exact: true }).click();
  await reset.waitForURL(/\/login\?email=user%40example.com&notice=password-reset$/);
  assert.equal(await reset.locator('#email').inputValue(), 'user@example.com');
  await reset.getByRole('status').filter({ hasText: 'Your password was changed.' }).waitFor();
  await reset.locator('input[name=redirectTo]').evaluate(el => { el.value = '/__test/session'; });
  await reset.locator('#password').fill('new-test-password');
  await reset.getByRole('button', { name: 'Sign in', exact: true }).click();
  await reset.waitForURL(base + '/__test/session');
  // Restore the fixture password through the real reset route for repeat runs.
  await reset.goto(base + '/reset-password');
  await reset.locator('#email').fill('user@example.com');
  await reset.getByRole('button', { name: 'Email me a code', exact: true }).click();
  await reset.locator('#confirmPassword').waitFor();
  await reset.locator('#code').fill('123456');
  await reset.locator('#password').fill('test-password');
  await reset.locator('#confirmPassword').fill('test-password');
  await reset.getByRole('button', { name: 'Change password', exact: true }).click();
  await reset.waitForURL(/\/login\?email=/);
  await context.close();
  assert.deepEqual(errors, [], 'no browser script errors');
  console.log('PASS: desktop/mobile code editing, selection, undo, paste, rejection, resend, code login, reset, native forms and accessibility');
} finally {
  await browser.close();
}
