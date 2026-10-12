import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { chromium } from 'playwright-core';

const base = process.argv[2]?.replace(/\/$/, '');
assert(base, 'Pass the running test-only C# host URL');
const browser = await chromium.launch({ headless: true });
const errors = [];
const results = [];
const runs = 5;

async function login(page) {
  const response = await page.goto(base + '/login');
  assert.equal(response.status(), 200);
  await page.locator('#email').fill('mario@terrascale.tech');
  await page.locator('#password').fill('terrascale');
  await Promise.all([
    page.waitForURL(base + '/'),
    page.getByRole('button', { name: 'Sign in', exact: true }).click()
  ]);
}

const observers = () => {
  window.perf = { lcp: 0, cls: 0, longTasks: 0 };
  new PerformanceObserver(list => {
    for (const entry of list.getEntries()) window.perf.lcp = entry.startTime;
  }).observe({ type: 'largest-contentful-paint', buffered: true });
  new PerformanceObserver(list => {
    for (const entry of list.getEntries()) if (!entry.hadRecentInput) window.perf.cls += entry.value;
  }).observe({ type: 'layout-shift', buffered: true });
  new PerformanceObserver(list => {
    for (const entry of list.getEntries()) window.perf.longTasks += entry.duration;
  }).observe({ type: 'longtask', buffered: true });
};

try {
  const inventory = await browser.newContext();
  const page = await inventory.newPage();
  await login(page);
  const response = await page.goto(base + '/databases');
  assert.equal(response.status(), 200);
  const database = await page.getByRole('link', { name: /^core-primary\b/ }).getAttribute('href');
  assert.match(database, /^\/databases\/[0-9A-HJKMNP-TV-Z]{26}$/);
  await inventory.close();
  const paths = ['/login', '/', '/databases', database + '/sql', '/usage'];
  for (const profile of ['desktop', 'mobile']) {
    for (const path of paths) {
      for (let run = 0; run < runs; run++) {
        const context = await browser.newContext({ viewport: profile === 'mobile'
          ? { width: 390, height: 844 } : { width: 1440, height: 900 } });
        try {
          const page = await context.newPage();
          page.on('pageerror', error => errors.push(error.message));
          if (path !== '/login') await login(page);
          const cdp = await context.newCDPSession(page);
          await cdp.send('Network.enable');
          await cdp.send('Network.clearBrowserCache');
          if (profile === 'mobile') {
            await cdp.send('Emulation.setCPUThrottlingRate', { rate: 4 });
            await cdp.send('Network.emulateNetworkConditions', {
              offline: false, latency: 80, downloadThroughput: 200000, uploadThroughput: 100000
            });
          }
          await page.addInitScript(observers);
          for (const cache of ['cold', 'warm']) {
            const response = await page.goto(base + path, { waitUntil: 'domcontentloaded' });
            assert.equal(response.status(), 200, profile + ' ' + path);
            assert.equal(new URL(page.url()).pathname, path);
            await page.evaluate(() => document.fonts.ready);
            await page.waitForFunction(() => window.perf.lcp > 0, null, { timeout: 15000 });
            await page.waitForTimeout(500);
            const measured = await page.evaluate(() => {
              const nav = performance.getEntriesByType('navigation')[0];
              return { ...window.perf, ttfb: nav.responseStart, domContentLoaded: nav.domContentLoadedEventEnd,
                bytes: nav.transferSize + performance.getEntriesByType('resource').reduce((n, r) => n + r.transferSize, 0) };
            });
            for (const [name, value] of Object.entries(measured))
              assert(Number.isFinite(value) && value >= 0, name + ' must be an actual browser measurement');
            assert(measured.bytes > 0);
            results.push({ profile, path, run, cache, ...measured });
          }
        } finally { await context.close(); }
      }
    }
  }
  assert.deepEqual(errors, []);
  assert.equal(results.length, 100);
  const grouped = Map.groupBy(results, row => row.profile + ' ' + row.path + ' ' + row.cache);
  const summary = [...grouped].map(([key, rows]) => ({ key, ...Object.fromEntries(
    ['lcp', 'cls', 'ttfb', 'domContentLoaded', 'bytes', 'longTasks'].map(metric => {
      const values = rows.map(row => row[metric]).sort((a, b) => a - b);
      return [metric, Math.round(values[Math.ceil(values.length * 0.75) - 1] * 100) / 100];
    })) }));
  const output = resolve(new URL('../../test-results/dotnet/performance.json', import.meta.url).pathname);
  await mkdir(resolve(output, '..'), { recursive: true });
  await writeFile(output, JSON.stringify({ baseURL: base, runs, summary, results }, null, 2) + '\n');
  console.log('PASS: 100 real cold/warm desktop/mobile measurements; ' + output);
} finally { await browser.close(); }
