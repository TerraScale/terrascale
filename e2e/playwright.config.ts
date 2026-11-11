import { defineConfig } from '@playwright/test';
import { browserHome, dash, desktop, mailWait, mobile, production, sessionFile } from './support/stack';

const secretFree = { trace: 'off' } as const;

export default defineConfig({
  testDir: '.',
  outputDir: 'test-results',
  fullyParallel: true,
  forbidOnly: true,
  retries: 0,
  workers: 6,
  timeout: 60_000 + mailWait,
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
    { name: 'paid', testMatch: 'journey/paid.spec.ts', metadata: { setup: 'paid' }, use: secretFree },
    { name: 'finish', testMatch: 'journey/finish.spec.ts', metadata: { setup: 'setup' }, use: { storageState: sessionFile('setup'), ...secretFree } },
    {
      name: 'desktop-close',
      testMatch: 'journey/close.spec.ts',
      metadata: { setup: 'desktop-setup' },
      use: { storageState: sessionFile('desktop-setup'), ...secretFree },
    },
  ],
});
