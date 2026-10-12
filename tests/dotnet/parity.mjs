import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { isDeepStrictEqual, promisify } from 'node:util';
import { readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { chromium } from 'playwright-core';

const base = process.argv[2];
assert(base, 'Pass the running test-only C# demo host URL');
const root = resolve(new URL('../..', import.meta.url).pathname);
const dir = resolve(root, 'test-results/parity');
const reference = JSON.parse(await readFile(resolve(dir, 'reference.json'), 'utf8'));
const personas = JSON.parse(await readFile(resolve(dir, 'fixture-personas.json'), 'utf8'));
const snapshot = await readFile(resolve(root, 'tests/parity/snapshot.js'), 'utf8');
const captureSnapshot = new Function('return (' + snapshot + ')')();
// Fixed Chromium layout-test raster path, shared with the original Go capture.
const browser = await chromium.launch({ headless: true, args: ['--disable-gpu', '--disable-skia-runtime-opts', '--disable-partial-raster'] });
const results = [];
const run = promisify(execFile);
let context, page, response, previousKey;
const contexts = new Map();
let errors = [];
try {
  for (const expected of reference) {
    if (previousKey !== expected.key) {
      // The Go exporter uses one signed-in context for route probes and a
      // second for its populated account. Match that session lifetime rather
      // than creating a fresh persistent session for every captured route.
      const group = expected.setup === 'signed-in'
        ? expected.key.startsWith('seeded-') ? 'seeded' : 'routes'
        : 'anonymous';
      const existing = contexts.get(group);
      // Go starts each context with Playwright's default viewport, reuses its
      // populated/anonymous pages, and opens a new page for each route probe.
      if (page && (context !== existing || group === 'routes')) {
        await page.close();
        page = undefined;
      }
      context = existing ?? await browser.newContext();
      if (!existing) contexts.set(group, context);
      errors = [];
      if (!page) {
        page = await context.newPage();
        page.on('pageerror', error => errors.push(error.message));
      }
      if (expected.setup === 'signed-in' && !existing) {
        await page.goto(`${base}/login`);
        const persona = personas[group];
        assert(persona?.email && persona?.password, 'The original Go identity must be supplied for ' + group);
        await page.locator('#email').fill(persona.email);
        await page.locator('#password').fill(persona.password);
        // Observe the production route after signing in through its native
        // form; do not seed cookies or replace feature pages.
        await Promise.all([
          page.waitForURL(`${base}/api/v1/auth/me`),
          page.locator('input[name=redirectTo]').evaluate(el => { el.value = '/api/v1/auth/me'; }).then(() => page.getByRole('button', { name: 'Sign in', exact: true }).click())
        ]);
      }
      const continueCode = expected.setup === 'code-filled' && previousKey === 'code-verify';
      if (!continueCode) response = await page.goto(base + expected.path);
      if (['code-verify', 'code-filled', 'reset-complete'].includes(expected.setup) && !continueCode) {
        await page.locator('#email').fill('user@example.com');
        [response] = await Promise.all([
          page.waitForResponse(value => value.request().isNavigationRequest() && value.request().method() === 'POST'),
          page.getByRole('button', { name: 'Email me a code', exact: true }).click()
        ]);
        await page.getByRole('heading', { name: expected.setup === 'reset-complete' ? 'Choose a new password' : 'Check your email' }).waitFor();
        if (expected.setup !== 'reset-complete') await page.locator('.ts-email-code-ready').waitFor();
      }
      if (expected.setup === 'code-filled') {
        await page.locator('#code').fill('123456');
        await page.locator('.ts-email-code-slot').last().filter({ hasText: '6' }).waitFor();
      }
      if (expected.setup === 'login-rejected') {
        await page.locator('#email').fill('user@example.com');
        await page.locator('#password').fill('wrong-password');
        [response] = await Promise.all([
          page.waitForResponse(value => value.request().isNavigationRequest() && value.request().method() === 'POST'),
          page.getByRole('button', { name: 'Sign in', exact: true }).click()
        ]);
        await page.getByRole('alert').waitFor();
      }
      previousKey = expected.key;
    }
    await page.setViewportSize(expected.viewport);
    // Hidden dialog editors add form controls when Monaco finishes mounting.
    // Wait for their actual upgrade, just as the Go capture does.
    await page.waitForFunction(() => [...document.querySelectorAll('ts-code-editor')].every(editor => editor.hasAttribute('data-ready')));
    if (expected.key === 'seeded-sql') {
      await page.locator('#sql-editor ts-code-editor[data-ready]').waitFor({ state: 'attached' });
      await page.getByRole('button', { name: 'Format', exact: true }).waitFor();
    }
    await page.evaluate(() => document.fonts.ready);
    await page.evaluate(async () => { await Promise.all([...document.images].filter(img => img.getClientRects().length && img.getBoundingClientRect().top < innerHeight).map(img => img.decode().catch(() => {}))); });
    await page.evaluate(async () => {
      const freeze = async () => { for (const animation of document.getAnimations()) { await animation.ready; if (animation.effect.getComputedTiming().iterations === Infinity) { animation.pause(); await animation.ready; animation.currentTime = 0; } else { animation.finish(); } } };
      await freeze();
      await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
      await freeze();
      await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    });
    const screenshot = `${expected.key}-${expected.size}-csharp.png`;
    await writeFile(resolve(dir, screenshot), await stableScreenshot(page));
    const actual = await page.evaluate(captureSnapshot);
    const differences = Object.keys(expected.snapshot).filter(key => !isDeepStrictEqual(expected.snapshot[key], actual[key]));
    const pixels = JSON.parse((await run('python3', [resolve(root, 'tests/dotnet/compare_pixels.py'), resolve(dir, expected.screenshot), resolve(dir, screenshot), resolve(dir, `${expected.key}-${expected.size}-diff.png`)])).stdout);
    results.push({ key: expected.key, size: expected.size, path: expected.path, expected_status: expected.status, status: response.status(),
      matches: response.status() === expected.status && differences.length === 0 && pixels.matches && errors.length === 0,
      differences, pixels, errors, screenshot, expected: expected.snapshot, actual });
  }
} finally {
  await browser.close();
}
const passed = results.filter(value => value.matches).length;
await writeFile(resolve(dir, 'report.json'), JSON.stringify({ passed, total: results.length, results }, null, 2) + '\n');
console.log(`Go/C# parity: ${passed}/${results.length} exact desktop/mobile comparisons pass. Report: ${resolve(dir, 'report.json')}`);
for (const failure of results.filter(value => !value.matches)) {
  console.log(`FAIL ${failure.key}/${failure.size}: HTTP ${failure.status} (Go ${failure.expected_status}); ${failure.differences.join(', ')}; pixels ${failure.pixels.changed_pixels ?? 'size mismatch'}`);
}
process.exitCode = passed === results.length ? 0 : 1;

async function stableScreenshot(page) {
  // Require stability within this host. No frame is selected by its similarity
  // to the other host, and the subsequent pixel comparison remains exact.
  let previous;
  for (let attempt = 0; attempt < 6; attempt++) {
    const current = await page.screenshot({ fullPage: true, caret: 'hide' });
    if (previous?.equals(current)) return current;
    previous = current;
  }
  throw new Error('C# page did not produce two identical consecutive screenshots: ' + page.url());
}
