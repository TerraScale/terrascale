import { defineConfig } from '@playwright/test';
import { browserHome, dash, desktop, mailWait, mobile, production, sessionFile } from './support/stack';

const placementRequested = process.env.E2E_PLACEMENT_SELECTED === '1' || process.argv.includes('placement') || process.argv.includes('--project=placement');
if (placementRequested) process.env.E2E_PLACEMENT_SELECTED = '1';

const secretFree = { trace: 'off' } as const;

export default defineConfig({
  testDir: '.',
  outputDir: 'test-results',
  fullyParallel: true,
  forbidOnly: true,
  retries: 0,
  workers: 6,
  timeout: 60_000 + mailWait,
  globalTimeout: production ? 25 * 60_000 : 0,
  expect: { timeout: 20_000 },
  reporter: production ? [['list'], ['json', { outputFile: 'production-report/report.json' }]] : [['list'], ['html', { open: 'never' }]],
  globalTeardown: './support/teardown.ts',
  use: {
    baseURL: dash,
    locale: 'en-US',
    timezoneId: 'UTC',
    viewport: desktop,
    screenshot: 'off',
    trace: production ? 'off' : { mode: 'retain-on-failure', screenshots: false, snapshots: true, sources: true },
    video: 'off',
    launchOptions: { env: { ...process.env, HOME: browserHome } },
  },
  projects: [
    ...(placementRequested ? [{
      name: 'placement-setup',
      testMatch: 'journey/setup.spec.ts',
      teardown: 'placement-close',
      metadata: { setup: 'placement-setup' },
      use: secretFree,
    }, {
      name: 'placement',
      testMatch: 'placement/*.spec.ts',
      testIgnore: 'placement/close.spec.ts',
      workers: 1,
      dependencies: process.env.E2E_REGIONS === '2' ? ['placement-setup'] : [],
      metadata: { setup: 'placement-setup' },
      use: secretFree,
    }, {
      name: 'placement-close',
      testMatch: 'placement/close.spec.ts',
      metadata: { setup: 'placement-setup' },
      use: { storageState: sessionFile('placement-setup'), ...secretFree },
    }] : []),
    { name: 'setup', testMatch: 'journey/setup.spec.ts', teardown: 'finish', metadata: { setup: 'setup' }, use: secretFree },
    { name: 'desktop-setup', testMatch: 'journey/setup.spec.ts', teardown: 'desktop-close', metadata: { setup: 'desktop-setup' }, use: secretFree },
    {
      name: 'pages-desktop',
      testMatch: 'pages/*.spec.ts',
      dependencies: ['desktop-setup'],
      metadata: { setup: 'desktop-setup' },
      use: { storageState: sessionFile('desktop-setup') },
    },
    {
      name: 'pages-mobile',
      testMatch: 'pages/*.spec.ts',
      dependencies: ['setup'],
      metadata: { setup: 'setup' },
      use: { storageState: sessionFile('setup'), viewport: mobile, isMobile: true, hasTouch: true, deviceScaleFactor: 2 },
    },
    { name: 'hosted', testMatch: 'hosted/*.spec.ts', dependencies: ['setup'], metadata: { setup: 'setup' }, use: secretFree },
    {
      name: 'interactions',
      testMatch: 'interactions/*.spec.ts',
      dependencies: ['setup'],
      metadata: { setup: 'setup' },
      use: { storageState: sessionFile('setup'), ...secretFree },
    },
    { name: 'invitation', testMatch: 'journey/invitation.spec.ts', dependencies: ['setup'], metadata: { setup: 'setup' }, use: secretFree },
    { name: 'paid', testMatch: 'journey/paid.spec.ts', teardown: 'paid-finish', metadata: { setup: 'paid' }, use: secretFree },
    { name: 'paid-invitation', testMatch: 'journey/paid-invitation.spec.ts', dependencies: ['paid'], metadata: { setup: 'paid' }, use: secretFree },
    { name: 'finish', testMatch: 'journey/finish.spec.ts', metadata: { setup: 'setup' }, use: { storageState: sessionFile('setup'), ...secretFree } },
    { name: 'paid-finish', testMatch: 'journey/paid-finish.spec.ts', metadata: { setup: 'paid' }, use: { storageState: sessionFile('paid'), ...secretFree } },
    {
      name: 'desktop-close',
      testMatch: 'journey/close.spec.ts',
      metadata: { setup: 'desktop-setup' },
      use: { storageState: sessionFile('desktop-setup'), ...secretFree },
    },
  ],
});
