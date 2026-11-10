import { defineConfig } from '@playwright/test';
import { browserHome, dash, desktop, mobile, userSession } from './support/stack';

const secretFree = { trace: 'off', video: 'off' } as const;

export default defineConfig({
  testDir: '.',
  outputDir: 'test-results',
  fullyParallel: true,
  forbidOnly: true,
  retries: 0,
  workers: 6,
  timeout: 90_000,
  expect: { timeout: 20_000 },
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: dash,
    locale: 'en-US',
    timezoneId: 'UTC',
    viewport: desktop,
    screenshot: 'off',
    trace: { mode: 'retain-on-failure', screenshots: false, snapshots: true, sources: true },
    video: 'retain-on-failure',
    launchOptions: { env: { ...process.env, HOME: browserHome } },
  },
  projects: [
    { name: 'setup', testMatch: 'journey/setup.spec.ts', use: secretFree },
    { name: 'sdk-a', testMatch: 'sdk/group-a.spec.ts', dependencies: ['setup'] },
    { name: 'pages-desktop', testMatch: 'pages/*.spec.ts', dependencies: ['setup'], use: { storageState: userSession } },
    {
      name: 'pages-mobile',
      testMatch: 'pages/*.spec.ts',
      dependencies: ['setup'],
      use: { storageState: userSession, viewport: mobile, isMobile: true, hasTouch: true, deviceScaleFactor: 2 },
    },
    { name: 'hosted', testMatch: 'hosted/*.spec.ts', dependencies: ['setup'], use: secretFree },
    { name: 'interactions', testMatch: 'interactions/*.spec.ts', dependencies: ['setup'], use: { storageState: userSession, ...secretFree } },
    { name: 'invitation', testMatch: 'journey/invitation.spec.ts', dependencies: ['setup'], use: secretFree },
    {
      name: 'database',
      testMatch: 'journey/database.spec.ts',
      dependencies: ['sdk-a', 'pages-desktop', 'pages-mobile', 'hosted', 'interactions', 'invitation'],
      use: { storageState: userSession },
    },
    { name: 'sdk-b', testMatch: 'sdk/group-b.spec.ts', dependencies: ['database'] },
    { name: 'teardown', testMatch: 'journey/teardown.spec.ts', dependencies: ['sdk-b'], use: { storageState: userSession, ...secretFree } },
  ],
});
