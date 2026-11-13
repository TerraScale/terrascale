import { defineConfig } from '@playwright/test';
import { browserHome, dash, desktop, mailWait, mobile, production, sessionFile } from './support/stack';

const placementRequested = process.env.E2E_PLACEMENT_SELECTED === '1' || process.argv.includes('placement') || process.argv.includes('--project=placement');
if (placementRequested) process.env.E2E_PLACEMENT_SELECTED = '1';

const secretFree = { trace: 'off' } as const;
const faultRequested = process.env.E2E_FAULT_SELECTED === '1' || process.argv.includes('fault') || process.argv.includes('--project=fault');
if (faultRequested) process.env.E2E_FAULT_SELECTED = '1';
if (faultRequested && (production || process.env.E2E_REGIONS !== '2' || process.env.E2E_NODES_PER_REGION !== '2')) {
  throw new Error('the fault project requires the local four-node stack: E2E_REGIONS=2 E2E_NODES_PER_REGION=2');
}

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
  globalSetup: './support/client-address.ts',
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
    ...(faultRequested ? [{
      name: 'fault-setup',
      testMatch: 'fault/setup.spec.ts',
      teardown: 'fault-close',
      fullyParallel: false,
      metadata: { setup: 'fault-setup' },
      use: secretFree,
    }, {
      name: 'fault',
      testMatch: 'fault/fault.spec.ts',
      dependencies: ['fault-setup'],
      fullyParallel: false,
      metadata: { setup: 'fault-setup' },
      use: secretFree,
    }, {
      name: 'fault-close',
      testMatch: 'placement/close.spec.ts',
      metadata: { setup: 'fault-setup' },
      use: { storageState: sessionFile('fault-setup'), ...secretFree },
    }] : []),
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
