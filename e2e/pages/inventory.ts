import { createHash, randomBytes } from 'node:crypto';
import { expect, type Locator, type Page } from '@playwright/test';
import type { Journey } from '../support/state';

export type PagePattern = { path: RegExp; query?: Record<string, RegExp> };

type Identity = (page: Page, journey: Journey) => Locator;

export type PageState = {
  name: string;
  url: (journey: Journey, page: Page) => string | Promise<string>;
  covers: PagePattern;
  heading: Identity;
  path?: string;
  status?: number;
  shell?: false;
  signedOut?: true;
  data?: (page: Page, journey: Journey) => Promise<void>;
};

const ulid = '[0-9A-HJKMNP-TV-Z]{26}';
const id = new RegExp(`^${ulid}$`);
const optionalId = new RegExp(`^(${ulid})?$`);
const database = (journey: Journey) => `/databases/${journey.databaseId}`;
const databasePath = (tab: string) => new RegExp(`^/databases/${ulid}${tab ? `/${tab}` : ''}$`);
const mcpRedirect = 'http://127.0.0.1:9/callback';
const mcpResource = 'https://mcp.terrascale.tech/';

const title =
  (name: string | RegExp, level: 1 | 2 = 1): Identity =>
  (page) =>
    page.getByRole('heading', { level, name, exact: typeof name === 'string' });
const inDialog =
  (dialog: string, name: string): Identity =>
  (page) =>
    page.locator(`dialog#${dialog}[open]`).getByRole('heading', { level: 2, name, exact: true });
const region =
  (name: string): Identity =>
  (page) =>
    page.getByRole('region', { name, exact: true });
const currentTab =
  (navigation: string, label: string): Identity =>
  (page) =>
    page.getByRole('navigation', { name: navigation }).locator('[aria-current="page"]', { hasText: new RegExp(`^${label}$`) });

function stat(page: Page, label: string) {
  return page.locator('dl[aria-label="Workspace metrics"] .ts-stat').filter({ has: page.locator('dt', { hasText: label }) }).locator('dd');
}

async function databaseHeader(page: Page, journey: Journey): Promise<void> {
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(journey.databaseName);
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

async function mcpConsent(page: Page): Promise<string> {
  const registered = await page.request.post('/oauth/register', { data: { client_name: 'Journey MCP client', redirect_uris: [mcpRedirect] } });
  expect(registered.status(), 'MCP client registration').toBe(201);
  const client = (await registered.json()) as { client_id: string };
  const challenge = createHash('sha256').update(randomBytes(32).toString('base64url')).digest('base64url');
  const query = new URLSearchParams({
    response_type: 'code',
    client_id: client.client_id,
    redirect_uri: mcpRedirect,
    code_challenge: challenge,
    code_challenge_method: 'S256',
    state: 'journey',
    resource: mcpResource,
  });
  return `/oauth/authorize?${query}`;
}

const databaseTabs: [string, string, Identity][] = [
  ['tables', 'Tables', currentTab('Database sections', 'Tables')],
  ['grants', 'Grants', title('Grants', 2)],
  ['sql', 'SQL editor', title('Saved queries', 2)],
  ['functions', 'Functions', title('Functions', 2)],
  ['replication', 'Replication', title('Replication', 2)],
  ['conflicts', 'Conflicts', title('Conflicts', 2)],
  ['logs', 'Logs', title('Request logs', 2)],
  ['backups', 'Backups', title('Backup policy', 2)],
  ['branches', 'Forks', title('Forks', 2)],
  ['settings', 'Settings', title('Settings', 2)],
  ['danger', 'Danger zone', title('Danger zone', 2)],
  ['point-in-time', 'Point-in-time recovery', title('Restore database at UTC time', 2)],
];

const tabStates: Record<string, PagePattern['query']> = {
  sql: { result: optionalId, savedQueryId: optionalId },
  tables: { table: /^[A-Za-z0-9_]*$/ },
};

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

const organizationTabs: [string, Identity][] = [
  ['members', title(/^Organization members/, 2)],
  ['invitations', title(/^Invitations/, 2)],
  ['projects', region('Organization projects')],
  ['billing', title('Plans and billing', 2)],
  ['audit', title('Recent activity', 2)],
  ['sso', title('Single sign-on', 2)],
  ['danger', title('Danger zone', 2)],
];
const auditDefaults = { severity: /^(all)?$/, action: /^(all)?$/, search: /^$/ };
const auditFilteredTo = (action: string) => `/organization?tab=audit&search=&action=${action}&severity=all&range=7d`;
const auditRows = (page: Page) => page.locator('#recent-activity tbody tr');

async function onlyAction(page: Page, action: string): Promise<void> {
  await expect(auditRows(page).filter({ hasNotText: action }), `every row of the filtered audit is ${action}`).toHaveCount(0);
}

const profileTabs: [string, Identity][] = [
  ['profile', title('Profile details', 2)],
  ['security', title('Passkeys', 2)],
  ['sessions', title('Active sessions', 2)],
  ['access-tokens', title('Personal access tokens', 2)],
  ['danger', title('Delete account', 2)],
];

const organizationData: Record<string, PageState['data']> = {
  members: async (page, journey) => {
    const row = page.locator('main tr').filter({ hasText: journey.email });
    await expect(row).toHaveCount(1);
    await expect(row).toContainText('Owner');
  },
};

export const dashboardStates: readonly PageState[] = [
  {
    name: 'overview',
    url: () => '/',
    covers: { path: /^\/$/ },
    heading: title('Overview'),
    data: async (page) => {
      await expect(stat(page, 'Databases')).toHaveText('1');
    },
  },
  { name: 'overview over 7 days', url: () => '/?range=7d', covers: { path: /^\/$/ }, heading: title('Overview') },
  { name: 'overview connect', url: () => '/?tab=connect', covers: { path: /^\/$/, query: { tab: /^connect$/ } }, heading: title('Connect') },
  {
    name: 'databases',
    url: () => '/databases',
    covers: { path: /^\/databases$/ },
    heading: title('Databases'),
    data: async (page, journey) => {
      const row = page.locator('main tr').filter({ hasText: journey.databaseName });
      await expect(row).toHaveCount(1);
      await expect(row).toContainText('ready');
      await expect(row).toContainText(journey.databaseRegion, { ignoreCase: true });
    },
  },
  { name: 'databases over 7 days', url: () => '/databases?range=7d', covers: { path: /^\/databases$/ }, heading: title('Databases') },
  {
    name: 'new database dialog',
    url: () => '/databases?create=1',
    covers: { path: /^\/databases$/, query: { create: /^1$/ } },
    heading: inDialog('create-database', 'New database'),
  },
  {
    name: 'database overview',
    url: database,
    covers: { path: databasePath(''), query: { created: /^1?$/ } },
    heading: title('Connect', 2),
    data: async (page, journey) => {
      await databaseHeader(page, journey);
      await connectCard(page, journey);
    },
  },
  {
    name: 'database overview over 7 days',
    url: (journey) => `${database(journey)}?reads=7d&writes=7d&storage=7d&latency=7d`,
    covers: { path: databasePath('') },
    heading: title('Connect', 2),
  },
  ...databaseTabs.map(
    ([tab, label, heading]): PageState => ({
      name: `database ${label.toLowerCase()}`,
      url: (journey) => `${database(journey)}/${tab}`,
      covers: { path: databasePath(tab), query: tabStates[tab] },
      heading,
      data: async (page, journey) => {
        await expect(page).toHaveTitle(`${journey.databaseName} · ${label} · TerraScale`);
        await databaseHeader(page, journey);
      },
    }),
  ),
  {
    name: 'fork dialog',
    url: (journey) => `${database(journey)}/branches?fork=1`,
    covers: { path: databasePath('branches'), query: { fork: /^1$/ } },
    heading: inDialog('name-branch', 'Fork database'),
  },
  { name: 'file storage', url: () => '/storage', covers: { path: /^\/storage$/ }, heading: title('Files') },
  {
    name: 'api keys',
    url: () => '/api-keys',
    covers: { path: /^\/api-keys$/ },
    heading: title('API keys'),
    data: async (page, journey) => {
      const row = page.locator('main tr').filter({ hasText: journey.apiKeyName });
      await expect(row).toHaveCount(1);
      await expect(row.locator('.ts-apikeys-scopes .ts-badge')).toHaveText(['Full access']);
      expect((await page.content()).includes(journey.apiKey), 'the API key secret is never shown again').toBe(false);
    },
  },
  {
    name: 'new api key dialog with scopes',
    url: () => '/api-keys?create=key&scopes=sql%3Aread%2Csql%3Awrite',
    covers: { path: /^\/api-keys$/, query: { create: /^key$/, scopes: /^[a-z:,]*$/ } },
    heading: inDialog('create-key', 'New API key'),
    data: async (page) => {
      await expect(page.locator('#create-key input[name="scopes"][value="sql:write"]')).toBeChecked();
    },
  },
  { name: 'authentication', url: () => '/auth', covers: { path: /^\/auth$/ }, heading: title('Project authentication', 2) },
  ...authSections.map(
    ([section, label]): PageState => ({
      name: `authentication ${label.toLowerCase()}`,
      url: () => `/auth/${section}`,
      covers: { path: new RegExp(`^/auth/${section}$`) },
      heading: section === 'overview' ? title('Project authentication', 2) : title(`Authentication: ${label}`),
    }),
  ),
  {
    name: 'authentication under its alias',
    url: () => '/authentication',
    covers: { path: /^\/authentication$/ },
    heading: title('Project authentication', 2),
  },
  {
    name: 'authentication users under its alias',
    url: () => '/authentication/users',
    covers: { path: new RegExp(`^/authentication/(${authSections.map(([section]) => section).join('|')})$`) },
    heading: title('Authentication: Users'),
  },
  { name: 'analytics', url: () => '/analytics', covers: { path: /^\/analytics$/ }, heading: title('Analytics') },
  { name: 'analytics over 7 days', url: () => '/analytics?range=7d', covers: { path: /^\/analytics$/ }, heading: title('Analytics') },
  {
    name: 'analytics of the database',
    url: (journey) => `/analytics?project=${journey.projectId}&database=${journey.databaseId}&region=${journey.databaseRegion}`,
    covers: { path: /^\/analytics$/, query: { project: optionalId, database: optionalId, region: /^[A-Za-z0-9-]*$/ } },
    heading: title('Analytics'),
  },
  { name: 'usage', url: () => '/usage', covers: { path: /^\/usage$/ }, heading: title('By project', 2) },
  {
    name: 'usage of the project',
    url: (journey) => `/usage?tab=overview&project=${journey.projectId}&database=`,
    covers: { path: /^\/usage$/, query: { tab: /^overview$/, project: id, database: optionalId } },
    heading: title('By database', 2),
  },
  { name: 'organization', url: () => '/organization', covers: { path: /^\/organization$/ }, heading: title('Details', 2) },
  ...organizationTabs.map(
    ([tab, heading]): PageState => ({
      name: `organization ${tab}`,
      url: () => `/organization?tab=${tab}`,
      covers: { path: /^\/organization$/, query: { tab: new RegExp(`^${tab}$`), ...(tab === 'audit' ? auditDefaults : {}) } },
      heading,
      data: organizationData[tab],
    }),
  ),
  {
    name: 'organization audit second page',
    url: () => '/organization?tab=audit&range=7d&severity=all&action=all&search=&page=2',
    covers: { path: /^\/organization$/, query: { tab: /^audit$/, severity: /^all$/, action: /^all$/, search: /^$/, page: /^2$/ } },
    heading: title('Recent activity', 2),
  },
  {
    name: 'organization audit of database creations',
    url: () => auditFilteredTo('database.created'),
    covers: { path: /^\/organization$/, query: { tab: /^audit$/, search: /^$/, action: /^database\.created$/, severity: /^all$/ } },
    heading: title('Recent activity', 2),
    data: async (page, journey) => {
      await onlyAction(page, 'database.created');
      await expect(auditRows(page).filter({ hasText: 'Database created' }).filter({ hasText: journey.databaseName })).toHaveCount(1);
    },
  },
  {
    name: 'organization audit of API key creations',
    url: () => auditFilteredTo('api_key.created'),
    covers: { path: /^\/organization$/, query: { tab: /^audit$/, search: /^$/, action: /^api_key\.created$/, severity: /^all$/ } },
    heading: title('Recent activity', 2),
    data: async (page, journey) => {
      await onlyAction(page, 'api_key.created');
      await expect(auditRows(page).filter({ hasText: 'API key created' }).filter({ hasText: journey.apiKey.split('_')[0] })).toHaveCount(1);
    },
  },
  {
    name: 'new project dialog',
    url: () => '/organization?newProject=1',
    covers: { path: /^\/organization$/, query: { newProject: /^1$/ } },
    heading: inDialog('create-project', 'Create project'),
  },
  {
    name: 'new organization dialog',
    url: () => '/organization?newOrganization=organization',
    covers: { path: /^\/organization$/, query: { newOrganization: /^organization$/ } },
    heading: inDialog('create-organization', 'Create organization'),
  },
  {
    name: 'organization choice',
    url: () => '/organizations/choose',
    covers: { path: /^\/organizations\/choose$/ },
    heading: title('Choose an organization'),
    shell: false,
  },
  { name: 'project settings', url: () => '/project', covers: { path: /^\/project$/ }, heading: title('Project settings') },
  { name: 'project members', url: () => '/project/members', covers: { path: /^\/project\/members$/ }, heading: region('Explicit project grants') },
  { name: 'project invitations', url: () => '/project/invitations', covers: { path: /^\/project\/invitations$/ }, heading: title('Project invitations') },
  { name: 'profile', url: () => '/profile', covers: { path: /^\/profile$/ }, heading: title('Profile details', 2) },
  ...profileTabs.map(
    ([tab, heading]): PageState => ({
      name: `profile ${tab}`,
      url: () => `/profile?tab=${tab}`,
      covers: { path: /^\/profile$/, query: { tab: new RegExp(`^${tab}$`), status: /^([a-z-]+)?$/ } },
      heading,
    }),
  ),
  { name: 'notifications', url: () => '/notifications', covers: { path: /^\/notifications$/ }, heading: title('Notifications') },
  {
    name: 'unread notifications',
    url: () => '/notifications?status=unread',
    covers: { path: /^\/notifications$/, query: { status: /^unread$/ } },
    heading: title('Notifications'),
  },
  { name: 'alert rules', url: () => '/notifications/alerts', covers: { path: /^\/notifications\/alerts$/ }, heading: title('Create alert rule', 2) },
  { name: 'support', url: () => '/support', covers: { path: /^\/support$/ }, heading: title('Contact support') },
  {
    name: 'support about a request',
    url: (journey) => `/support?relatedRequestId=${journey.databaseId}`,
    covers: { path: /^\/support$/, query: { relatedRequestId: id } },
    heading: title('Contact support'),
  },
  {
    name: 'invitation signed in',
    url: () => `/invitations/${randomBytes(24).toString('base64url')}`,
    covers: { path: /^\/invitations\/[A-Za-z0-9_-]+$/ },
    heading: title('This invitation is no longer valid'),
    status: 404,
    shell: false,
  },
  {
    name: 'invitation signed out',
    url: () => `/invitations/${randomBytes(24).toString('base64url')}`,
    covers: { path: /^\/invitations\/[A-Za-z0-9_-]+$/ },
    heading: title('Sign in to view this invitation'),
    shell: false,
    signedOut: true,
  },
  {
    name: 'account deletion status without a receipt',
    url: () => '/account-deletion',
    covers: { path: /^\/account-deletion$/, query: { retry: /^[0-9]*$/ } },
    heading: title('Deletion status needs attention'),
    status: 403,
    shell: false,
  },
  {
    name: 'MCP client consent',
    url: (_journey, page) => mcpConsent(page),
    covers: {
      path: /^\/oauth\/authorize$/,
      query: {
        response_type: /^code$/,
        client_id: id,
        redirect_uri: new RegExp(`^${mcpRedirect.replaceAll('.', '\\.')}$`),
        code_challenge: /^[A-Za-z0-9_-]{43}$/,
        code_challenge_method: /^S256$/,
        state: /^journey$/,
        resource: new RegExp(`^${mcpResource.replaceAll('.', '\\.')}$`),
      },
    },
    heading: title('Connect an AI client'),
    shell: false,
  },
  {
    name: 'api reference',
    url: () => '/reference',
    covers: { path: /^\/reference\/?$/ },
    heading: region('Introduction'),
    path: '/reference/',
    shell: false,
    data: async (page) => {
      await expect(page).toHaveTitle('TerraScale API reference');
    },
  },
  {
    name: 'page not found',
    url: () => '/this-page-does-not-exist',
    covers: { path: /^\/this-page-does-not-exist$/ },
    heading: title('We can’t find that page.'),
    status: 404,
    data: async (page) => {
      await expect(page).toHaveTitle('Page not found · TerraScale');
    },
  },
  {
    name: 'platform administration',
    url: () => '/admin',
    covers: { path: /^\/admin$/ },
    heading: title('This request was not allowed'),
    status: 403,
    shell: false,
    data: async (page) => {
      await expect(page).toHaveTitle('This request was not allowed · TerraScale');
    },
  },
];
