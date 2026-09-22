import { chromium } from 'playwright';
import { mkdir, writeFile, readFile } from 'node:fs/promises';

const baseURL = process.env.DASHBOARD_E2E_BASE_URL ?? 'http://127.0.0.1:5439';
const output = process.env.PERF_OUTPUT ?? 'test-results/performance.json';
const runs = Number(process.env.PERF_RUNS ?? 5);
if (!Number.isInteger(runs) || runs < 1) throw new Error('PERF_RUNS must be a positive integer');
const browser = await chromium.launch();
const results = [];
try {
  for (const profile of ['desktop', 'mobile']) {
    for (const path of ['/login', '/', '/databases', '/data', '/settings', '/usage']) {
      for (let run = 0; run < runs; run++) {
        const context = await browser.newContext({ baseURL, viewport: profile === 'mobile'
          ? { width: 390, height: 844 } : { width: 1440, height: 900 } });
        if (path !== '/login') {
          await context.request.post('/api/v1/auth/password/login', {
            form: { email: 'mario@terrascale.tech', password: 'terrascale' },
          });
        }
        const page = await context.newPage();
        await page.bringToFront();
        const cdp = await context.newCDPSession(page);
        await cdp.send('Network.enable');
        if (profile === 'mobile') {
          await cdp.send('Emulation.setCPUThrottlingRate', { rate: 4 });
          await cdp.send('Network.emulateNetworkConditions', {
            offline: false, latency: 80, downloadThroughput: 200_000, uploadThroughput: 100_000,
          });
        }
        await page.addInitScript(() => {
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
        });
        for (const cache of ['cold', 'warm']) {
          const response = await page.goto(path);
          if (!response?.ok() || new URL(page.url()).pathname !== path) {
            throw new Error(`Failed measurement navigation: ${path} -> ${page.url()}`);
          }
          await page.evaluate(() => document.fonts.ready);
          await page.waitForFunction(() => window.perf.lcp > 0, { timeout: 15_000 });
          await page.waitForTimeout(500);
          results.push({ profile, path, run, cache, ...await page.evaluate(() => {
            const nav = performance.getEntriesByType('navigation')[0];
            const resources = performance.getEntriesByType('resource');
            return { ...window.perf, ttfb: nav.responseStart, domContentLoaded: nav.domContentLoadedEventEnd,
              bytes: nav.transferSize + resources.reduce((n, r) => n + r.transferSize, 0),
              resources: resources.map(r => ({ url: new URL(r.name).pathname,
                bytes: r.transferSize, decoded: r.decodedBodySize, duration: r.duration })),
              serverTiming: nav.serverTiming };
          }) });
        }
        await context.close();
      }
    }
  }
} finally { await browser.close(); }
const p75 = values => values.sort((a, b) => a - b)[Math.ceil(values.length * .75) - 1];
const summary = [];
for (const key of new Set(results.map(r => `${r.profile} ${r.path} ${r.cache}`))) {
  const rows = results.filter(r => `${r.profile} ${r.path} ${r.cache}` === key);
  summary.push({ key, ...Object.fromEntries(['lcp', 'cls', 'ttfb', 'domContentLoaded', 'bytes', 'longTasks']
    .map(metric => [metric, Math.round(p75(rows.map(r => r[metric])) * 100) / 100])) });
}
await mkdir('test-results', { recursive: true });
await writeFile(output, JSON.stringify({ baseURL, runs, summary, results }, null, 2));
console.table(summary);
if (process.env.PERF_BASELINE) {
  const baseline = JSON.parse(await readFile(process.env.PERF_BASELINE, 'utf8'));
  const regressions = summary.flatMap(row => {
    const before = baseline.summary.find(old => old.key === row.key);
    if (!before) return [`Missing baseline: ${row.key}`];
    return ['lcp', 'domContentLoaded'].filter(metric => row[metric] > before[metric] * 1.2 + 100)
      .map(metric => `${row.key}: ${metric} ${before[metric]} -> ${row[metric]}`)
      .concat(row.cls > Math.max(.1, before.cls + .02) ? [`${row.key}: CLS ${row.cls}`] : []);
  });
  if (regressions.length) { console.error(regressions.join('\n')); process.exitCode = 1; }
}
