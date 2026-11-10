import { expect, type Page } from '@playwright/test';
import type { Journey } from '../support/state';

export type PagePattern = { path: RegExp; query?: Record<string, RegExp> };

export type PageState = {
  name: string;
  url: (journey: Journey) => string;
  covers: PagePattern;
  status?: number;
  data?: (page: Page, journey: Journey) => Promise<void>;
};

const ulid = '[0-9A-HJKMNP-TV-Z]{26}';
const id = new RegExp(`^${ulid}$`);
const optionalId = new RegExp(`^(${ulid})?$`);
const database = (journey: Journey) => `/databases/${journey.databaseId}`;
const databasePath = (tab: string) => new RegExp(`^/databases/${ulid}${tab ? `/${tab}` : ''}$`);

function stat(page: Page, label: string) {
  return page.locator('dl[aria-label="Workspace metrics"] .ts-stat').filter({ has: page.locator('dt', { hasText: label }) }).locator('dd');
}

async function heading(page: Page, text: string): Promise<void> {
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(text);
}

async function databaseHeader(page: Page, journey: Journey): Promise<void> {
  await heading(page, journey.databaseName);
  const header = page.locator('main');
  await expect(header.getByText('ready', { exact: true }).first()).toBeVisible();
  await expect(header.getByText(new RegExp(`^${journey.databaseRegion}$`, 'i')).first()).toBeVisible();
}

async function connectCard(page: Page, journey: Journey): Promise<void> {
  const tabs = page.locator('#db-connect-sdk [role=tab]');
  await expect(tabs).toHaveCount(5);
  for (const tab of await tabs.all()) {
    await tab.click();
    await expect(tab).toHaveAttribute('aria-selected', 'true');
    const panel = page.locator(`#${await tab.getAttribute('aria-controls')}`);
    await expect(panel).toBeVisible();
    await expect(panel).toContainText(journey.databaseName);
  }
}

const databaseTabs: [string, string][] = [
  ['tables', 'Tables'],
  ['grants', 'Grants'],
  ['sql', 'SQL editor'],
  ['functions', 'Functions'],
  ['replication', 'Replication'],
  ['conflicts', 'Conflicts'],
  ['logs', 'Logs'],
  ['backups', 'Backups'],
  ['branches', 'Forks'],
  ['settings', 'Settings'],
  ['danger', 'Danger zone'],
  ['point-in-time', 'Point-in-time recovery'],
];

const authSections: [string, string][] = [
  ['overview', 'Overview'],
  ['users', 'Users'],
  ['sessions', 'Sessions'],
  ['sign-in', 'Sign-in methods'],
  ['social', 'Social sign-in'],
  ['configuration', 'Configuration'],
  ['clients', 'OAuth applications'],
  ['webhooks', 'Webhooks'],
  ['branding', 'Branding'],
  ['danger', 'Danger zone'],
];

const organizationTabs = ['members', 'invitations', 'projects', 'billing', 'audit', 'sso', 'danger'];
const profileTabs = ['profile', 'security', 'sessions', 'access-tokens', 'danger'];

const organizationData: Record<string, PageState['data']> = {
  members: async (page, journey) => {
    const row = page.locator('main tr').filter({ hasText: journey.email });
    await expect(row).toHaveCount(1);
    await expect(row).toContainText('Owner');
  },
  audit: async (page, journey) => {
    const rows = page.locator('main tr');
    await expect(rows.filter({ hasText: 'Database created' }).filter({ hasText: journey.databaseName })).toHaveCount(1);
    await expect(rows.filter({ hasText: 'API key created' }).first()).toBeVisible();
  },
};

export const dashboardStates: readonly PageState[] = [
  {
    name: 'overview',
    url: () => '/',
    covers: { path: /^\/$/ },
    data: async (page) => {
      await heading(page, 'Overview');
      await expect(stat(page, 'Databases')).toHaveText('1');
    },
  },
  { name: 'overview over 7 days', url: () => '/?range=7d', covers: { path: /^\/$/ } },
  { name: 'overview connect', url: () => '/?tab=connect', covers: { path: /^\/$/, query: { tab: /^connect$/ } } },
  {
    name: 'databases',
    url: () => '/databases',
    covers: { path: /^\/databases$/ },
    data: async (page, journey) => {
      const row = page.locator('main tr').filter({ hasText: journey.databaseName });
      await expect(row).toHaveCount(1);
      await expect(row).toContainText('ready');
      await expect(row).toContainText(journey.databaseRegion, { ignoreCase: true });
    },
  },
  { name: 'databases over 7 days', url: () => '/databases?range=7d', covers: { path: /^\/databases$/ } },
  {
    name: 'new database dialog',
    url: () => '/databases?create=1',
    covers: { path: /^\/databases$/, query: { create: /^1$/ } },
    data: async (page) => {
      await expect(page.locator('#create-database[open]')).toBeVisible();
    },
  },
  {
    name: 'database overview',
    url: database,
    covers: { path: databasePath(''), query: { created: /^1?$/ } },
    data: async (page, journey) => {
      await databaseHeader(page, journey);
      await connectCard(page, journey);
    },
  },
  {
    name: 'database overview over 7 days',
    url: (journey) => `${database(journey)}?reads=7d&writes=7d&storage=7d&latency=7d`,
    covers: { path: databasePath('') },
  },
  ...databaseTabs.map(([tab, title]): PageState => ({
    name: `database ${title.toLowerCase()}`,
    url: (journey) => `${database(journey)}/${tab}`,
    covers: { path: databasePath(tab) },
    data: async (page, journey) => {
      await expect(page).toHaveTitle(`${journey.databaseName} · ${title} · TerraScale`);
      await databaseHeader(page, journey);
    },
  })),
  {
    name: 'fork dialog',
    url: (journey) => `${database(journey)}/branches?fork=1`,
    covers: { path: databasePath('branches'), query: { fork: /^1$/ } },
    data: async (page) => {
      await expect(page.locator('#name-branch[open]')).toBeVisible();
    },
  },
  { name: 'file storage', url: () => '/storage', covers: { path: /^\/storage$/ } },
  {
    name: 'api keys',
    url: () => '/api-keys',
    covers: { path: /^\/api-keys$/ },
    data: async (page, journey) => {
      await expect(page.locator('main tr').filter({ hasText: journey.apiKeyName })).toHaveCount(1);
      expect((await page.content()).includes(journey.apiKey), 'the API key secret is never shown again').toBe(false);
    },
  },
  {
    name: 'new api key dialog with scopes',
    url: () => '/api-keys?create=key&scopes=sql%3Aread%2Csql%3Awrite',
    covers: { path: /^\/api-keys$/, query: { create: /^key$/, scopes: /^[a-z:,]*$/ } },
    data: async (page) => {
      await expect(page.locator('#create-key[open]')).toBeVisible();
      await expect(page.locator('#create-key input[name="scopes"][value="sql:write"]')).toBeChecked();
    },
  },
  { name: 'authentication', url: () => '/auth', covers: { path: /^\/auth$/ } },
  ...authSections.map(([section, title]): PageState => ({
    name: `authentication ${title.toLowerCase()}`,
    url: () => `/auth/${section}`,
    covers: { path: new RegExp(`^/auth/${section}$`) },
  })),
  { name: 'analytics', url: () => '/analytics', covers: { path: /^\/analytics$/ } },
  { name: 'analytics over 7 days', url: () => '/analytics?range=7d', covers: { path: /^\/analytics$/ } },
  {
    name: 'analytics of the database',
    url: (journey) => `/analytics?project=${journey.projectId}&database=${journey.databaseId}&region=local`,
    covers: { path: /^\/analytics$/, query: { project: optionalId, database: optionalId, region: /^[A-Za-z]*$/ } },
  },
  { name: 'usage', url: () => '/usage', covers: { path: /^\/usage$/ } },
  {
    name: 'usage of the project',
    url: (journey) => `/usage?tab=overview&project=${journey.projectId}&database=`,
    covers: { path: /^\/usage$/, query: { tab: /^overview$/, project: id, database: optionalId } },
  },
  { name: 'organization', url: () => '/organization', covers: { path: /^\/organization$/ } },
  ...organizationTabs.map(
    (tab): PageState => ({
      name: `organization ${tab}`,
      url: () => `/organization?tab=${tab}`,
      covers: { path: /^\/organization$/, query: { tab: new RegExp(`^${tab}$`) } },
      data: organizationData[tab],
    }),
  ),
  {
    name: 'new project dialog',
    url: () => '/organization?newProject=1',
    covers: { path: /^\/organization$/, query: { newProject: /^1$/ } },
  },
  {
    name: 'new organization dialog',
    url: () => '/organization?newOrganization=organization',
    covers: { path: /^\/organization$/, query: { newOrganization: /^organization$/ } },
  },
  { name: 'project settings', url: () => '/project', covers: { path: /^\/project$/ } },
  { name: 'project members', url: () => '/project/members', covers: { path: /^\/project\/members$/ } },
  { name: 'project invitations', url: () => '/project/invitations', covers: { path: /^\/project\/invitations$/ } },
  { name: 'profile', url: () => '/profile', covers: { path: /^\/profile$/ } },
  ...profileTabs.map(
    (tab): PageState => ({
      name: `profile ${tab}`,
      url: () => `/profile?tab=${tab}`,
      covers: { path: /^\/profile$/, query: { tab: new RegExp(`^${tab}$`) } },
    }),
  ),
  { name: 'notifications', url: () => '/notifications', covers: { path: /^\/notifications$/ } },
  {
    name: 'unread notifications',
    url: () => '/notifications?status=unread',
    covers: { path: /^\/notifications$/, query: { status: /^unread$/ } },
  },
  { name: 'alert rules', url: () => '/notifications/alerts', covers: { path: /^\/notifications\/alerts$/ } },
  { name: 'support', url: () => '/support', covers: { path: /^\/support$/ } },
  {
    name: 'support about a request',
    url: (journey) => `/support?relatedRequestId=${journey.databaseId}`,
    covers: { path: /^\/support$/, query: { relatedRequestId: id } },
  },
  { name: 'api reference', url: () => '/reference', covers: { path: /^\/reference\/?$/ } },
  { name: 'page not found', url: () => '/this-page-does-not-exist', covers: { path: /^\/this-page-does-not-exist$/ }, status: 404 },
  { name: 'platform administration', url: () => '/admin', covers: { path: /^\/admin$/ }, status: 403 },
];
