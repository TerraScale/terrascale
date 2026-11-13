import { createHash, randomBytes } from 'node:crypto';
import { expect, type Locator, type Page } from '@playwright/test';
import { regionsSettled } from '../support/overview';
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

async function regionPicked(page: Page, map: string, journey: Journey): Promise<void> {
  const picked = page.locator(`#${map} .ts-region-callout:is([data-placed], [data-docked]) input[name="regions"]:checked`);
  await expect(picked).toHaveCount(1);
  await expect(picked).toHaveValue(new RegExp(`^${journey.databaseRegion}$`, 'i'));
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
  functions: { function: /^([a-z0-9][a-z0-9_-]{0,62})?$/ },
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
  ['mfa-sessions', 'MFA and sessions'],
  ['attack-protection', 'Attack protection'],
  ['clients', 'OAuth applications'],
  ['webhooks', 'Webhooks'],
  ['logs', 'Logs'],
  ['branding', 'Branding'],
  ['danger', 'Danger zone'],
];

const organizationTabs: [string, Identity][] = [
  ['members', title(/^Organization members/, 2)],
  ['invitations', title(/^Invitations/, 2)],
  ['projects', region('Organization projects')],
  ['billing', title('Current plan', 2)],
  ['audit', title('Recent activity', 2)],
  ['sso', title('Single sign-on', 2)],
  ['danger', title('Danger zone', 2)],
];
const auditDefaults = { severity: /^(all)?$/, action: /^(all)?$/, search: /^$/ };
const auditFilteredTo = (action: string) => `/organization?tab=audit&search=&action=${action}&severity=all&range=7d`;
const auditRows = (page: Page) => page.locator('#recent-activity tr[data-audit-action]');

async function onlyAction(page: Page, action: string): Promise<void> {
  await expect(page.locator(`#recent-activity tr[data-audit-action]:not([data-audit-action="${action}"])`), `every row of the filtered audit is ${action}`).toHaveCount(0);
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
  invitations: async (page) => {
    await expect(page.getByText('No invitations yet.', { exact: true })).toBeVisible();
  },
  projects: async (page, journey) => {
    const row = page.locator('#projects tr').filter({ hasText: journey.projectName });
    await expect(row).toHaveCount(1);
    await expect(row).toContainText(journey.projectId);
  },
  billing: async (page) => {
    await expect(page.locator('#billing-summary .ts-billing-plan-name')).toHaveText('Free');
    await expect(page.getByText('No invoices yet.', { exact: true })).toBeVisible();
  },
  audit: async (page, journey) => {
    const rows = auditRows(page).filter({ hasText: journey.databaseName });
    await expect(rows.first()).toBeVisible();
    await expect(rows.filter({ hasText: 'Database created' })).toHaveCount(1);
  },
  sso: async (page) => {
    await expect(page.getByText('No connection', { exact: true })).toBeVisible();
  },
  danger: async (page) => {
    await expect(page.locator('#organization-danger')).toContainText('Invite another member before transferring ownership.');
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
      await regionsSettled(page);
    },
  },
  {
    name: 'overview over 7 days',
    url: () => '/?range=7d',
    covers: { path: /^\/$/ },
    heading: title('Overview'),
    data: async (page) => {
      await expect(stat(page, 'Databases')).toHaveText('1');
      await regionsSettled(page);
    },
  },
  {
    name: 'overview connect',
    url: () => '/?tab=connect',
    covers: { path: /^\/$/, query: { tab: /^connect$/ } },
    heading: title('Connect'),
    data: async (page, journey) => {
      await expect(page.locator('#connect-database')).toHaveValue(journey.databaseName);
    },
  },
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
  {
    name: 'databases over 7 days',
    url: () => '/databases?range=7d',
    covers: { path: /^\/databases$/ },
    heading: title('Databases'),
    data: async (page, journey) => {
      await expect(page.locator('main tr').filter({ hasText: journey.databaseName })).toHaveCount(1);
    },
  },
  {
    name: 'new database dialog',
    url: () => '/databases?create=1',
    covers: { path: /^\/databases$/, query: { create: /^1$/ } },
    heading: inDialog('create-database', 'New database'),
    data: (page, journey) => regionPicked(page, 'create-database-regions', journey),
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
    data: async (page, journey) => {
      await databaseHeader(page, journey);
    },
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
        if (tab === 'replication') {
          await expect(page.getByRole('heading', { level: 2, name: 'Regions', exact: true })).toBeVisible();
          await regionPicked(page, 'database-regions', journey);
        }
      },
    }),
  ),
  {
    name: 'fork dialog',
    url: (journey) => `${database(journey)}/branches?fork=1`,
    covers: { path: databasePath('branches'), query: { fork: /^1$/ } },
    heading: inDialog('name-branch', 'Fork database'),
    data: async (page, journey) => {
      await expect(page.locator('#name-branch input[name="databaseId"]')).toHaveValue(journey.databaseId);
    },
  },
  {
    name: 'file storage',
    url: () => '/storage',
    covers: { path: /^\/storage$/ },
    heading: title('Files'),
    data: async (page, journey) => {
      await expect(page.locator('main.ts-files')).toHaveAttribute('data-project-id', journey.projectId);
      await expect(page.getByText('No buckets yet.', { exact: true })).toBeVisible();
    },
  },
  {
    name: 'api keys',
    url: () => '/api-keys',
    covers: { path: /^\/api-keys$/ },
    heading: title('API keys'),
    data: async (page, journey) => {
      const row = page.locator('main tr').filter({ hasText: journey.apiKeyName });
      await expect(row).toHaveCount(1);
      await expect(row.locator('.ts-apikeys-access')).toHaveText(['Full']);
      expect((await page.content()).includes(journey.apiKey), 'the API key secret is never shown again').toBe(false);
    },
  },
  {
    name: 'api key details',
    url: (journey) => `/api-keys/${journey.apiKey.split('_')[0]}`,
    covers: { path: new RegExp(`^/api-keys/${ulid}$`) },
    heading: (page, journey) => page.getByRole('heading', { level: 1, name: journey.apiKeyName, exact: true }),
    data: async (page, journey) => {
      const details = page.locator('#key-details');
      await expect(details.locator('dd.ts-mono')).toHaveText(journey.apiKey.split('_')[0]);
      await expect(details.locator('.ts-apikeys-scopes .ts-badge')).toHaveText(['Full access']);
      await expect(page.locator('#key-allowed-ips')).toContainText('Any IP');
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
      await expect(page.getByRole('checkbox', { name: 'Authentication read', exact: true })).toBeVisible();
      await expect(page.getByRole('checkbox', { name: 'Authentication write', exact: true })).toBeVisible();
    },
  },
  {
    name: 'authentication',
    url: () => '/auth',
    covers: { path: /^\/auth$/ },
    heading: title('Project authentication', 2),
    data: async (page, journey) => {
      await expect(page.locator('.ts-idp-kv dd').first()).toHaveText(journey.projectName);
    },
  },
  ...authSections.map(
    ([section, label]): PageState => ({
      name: `authentication ${label.toLowerCase()}`,
      url: () => `/auth/${section}`,
      covers: { path: new RegExp(`^/auth/${section}$`), ...(section === 'logs' ? { query: { after: optionalId } } : {}), ...(section === 'attack-protection' ? { query: { saved: /^(1)?$/ } } : {}) },
      heading: section === 'overview' ? title('Project authentication', 2) : title(`Authentication: ${label}`),
      data: async (page, journey) => {
        if (section === 'overview') await expect(page.locator('.ts-idp-kv dd').first()).toHaveText(journey.projectName);
        else if (section === 'logs') await expect(page.getByText('No events in the last 30 days.', { exact: true })).toBeVisible();
        else if (section === 'mfa-sessions') {
          await expect(page.locator('input[name=require_mfa]')).not.toBeChecked();
          await expect(page.locator('input[name=allow_totp]')).toBeChecked();
          await expect(page.locator('input[name=allow_passkey]')).toBeChecked();
          await expect(page.locator('#auth-inactivity')).toHaveValue('');
        }
        else if (section === 'attack-protection') {
          await expect(page.locator('input[name=breached_passwords]')).toBeChecked();
          await expect(page.locator('input[name=account_lockout]')).toBeChecked();
          await expect(page.locator('input[name=new_device_email]')).not.toBeChecked();
          await expect(page.locator('input[name=bot_challenge]')).not.toBeChecked();
        }
        else if (section === 'users') await expect(page.getByText('No users yet.', { exact: true })).toBeVisible();
        else if (section === 'danger') {
          await expect(page.getByText('No records.', { exact: true })).toHaveCount(3);
          await expect(page.locator('main')).toHaveAttribute('data-auth-section', 'danger');
        }
      },
    }),
  ),
  {
    name: 'authentication under its alias',
    url: () => '/authentication',
    covers: { path: /^\/authentication$/ },
    heading: title('Project authentication', 2),
    data: async (page, journey) => {
      await expect(page.locator('.ts-idp-kv dd').first()).toHaveText(journey.projectName);
    },
  },
  {
    name: 'authentication users under its alias',
    url: () => '/authentication/users',
    covers: { path: new RegExp(`^/authentication/(${authSections.map(([section]) => section).join('|')})$`) },
    heading: title('Authentication: Users'),
    data: async (page) => {
      await expect(page.getByText('No users yet.', { exact: true })).toBeVisible();
    },
  },
  {
    name: 'analytics',
    url: () => '/analytics',
    covers: { path: /^\/analytics$/ },
    heading: title('Analytics'),
    data: async (page, journey) => {
      await expect(page.locator('#analytics-database')).toContainText(journey.databaseName);
    },
  },
  {
    name: 'analytics over 7 days',
    url: () => '/analytics?range=7d',
    covers: { path: /^\/analytics$/ },
    heading: title('Analytics'),
    data: async (page, journey) => {
      await expect(page.locator('main')).toContainText(journey.databaseName);
    },
  },
  {
    name: 'analytics of the database',
    url: (journey) => `/analytics?project=${journey.projectId}&database=${journey.databaseId}&region=${journey.databaseRegion}`,
    covers: { path: /^\/analytics$/, query: { project: optionalId, database: optionalId, region: /^[A-Za-z0-9-]*$/ } },
    heading: title('Analytics'),
    data: async (page, journey) => {
      await expect(page.locator('main')).toContainText(journey.databaseName);
    },
  },
  {
    name: 'usage',
    url: () => '/usage',
    covers: { path: /^\/usage$/ },
    heading: title('By project', 2),
    data: async (page, journey) => {
      await expect(page.locator('#usage-allowance-title')).toHaveText('Free plan allowance');
      await expect(page.locator('main')).toContainText(journey.projectName);
    },
  },
  {
    name: 'usage of the project',
    url: (journey) => `/usage?tab=overview&project=${journey.projectId}&database=`,
    covers: { path: /^\/usage$/, query: { tab: /^overview$/, project: id, database: optionalId } },
    heading: title('By database', 2),
    data: async (page, journey) => {
      await expect(page.locator('#usage-allowance-title')).toHaveText('Free plan allowance');
      await expect(page.locator('main')).toContainText(journey.databaseName);
    },
  },
  {
    name: 'organization',
    url: () => '/organization',
    covers: { path: /^\/organization$/ },
    heading: title('Details', 2),
    data: async (page, journey) => {
      await expect(page.locator('#organization-details')).toContainText(journey.organizationName);
      await expect(page.locator('#organization-details')).toContainText(journey.email);
      await expect(page.locator('.ts-org-plan-row strong')).toHaveText('Free');
    },
  },
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
    name: 'invite member dialog',
    url: () => '/organization?tab=members&invite=1',
    covers: { path: /^\/organization$/, query: { tab: /^members$/, invite: /^1$/ } },
    heading: inDialog('team-invite', 'Invite member'),
    data: async (page) => {
      await expect(page.locator('#team-invite').getByLabel('Email', { exact: true })).toHaveValue('');
      await expect(page.locator('#team-invite').getByLabel('Role', { exact: true })).toHaveValue('member');
      await expect(page.locator('#team-invite-error')).toHaveCount(0);
    },
  },
  {
    name: 'organization audit second page',
    url: () => '/organization?tab=audit&range=7d&severity=all&action=all&search=&page=2',
    covers: { path: /^\/organization$/, query: { tab: /^audit$/, severity: /^all$/, action: /^all$/, search: /^$/, page: /^2$/ } },
    heading: title('Recent activity', 2),
    data: async (page, journey) => {
      const rows = auditRows(page);
      await expect(rows.first()).toBeVisible();
      await expect(rows.filter({ hasText: journey.email }).first()).toBeVisible();
    },
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
    data: async (page) => {
      await expect(page.locator('#create-project input[name="name"]')).toHaveValue('');
      const regions = page.locator('#create-project input[name="regions"]');
      await expect(regions.first()).toBeVisible();
      expect(await regions.count()).toBeGreaterThan(0);
      await expect(page.locator('#create-project').getByText('Primary region')).toHaveCount(0);
      await expect(page.locator('#create-project').getByText('Backup region')).toHaveCount(0);
    },
  },
  {
    name: 'new organization dialog',
    url: () => '/organization?newOrganization=organization',
    covers: { path: /^\/organization$/, query: { newOrganization: /^organization$/ } },
    heading: inDialog('create-organization', 'Create organization'),
    data: async (page) => {
      await expect(page.locator('#create-organization input[name="name"]')).toHaveValue('');
    },
  },
  {
    name: 'organization choice',
    url: () => '/organizations/choose',
    covers: { path: /^\/organizations\/choose$/ },
    heading: title('Choose an organization'),
    shell: false,
    data: async (page, journey) => {
      await expect(page.locator('.ts-org-picker-name').filter({ hasText: journey.organizationName })).toHaveCount(1);
    },
  },
  {
    name: 'project settings',
    url: () => '/project',
    covers: { path: /^\/project$/ },
    heading: title('Project settings'),
    data: async (page, journey) => {
      await expect(page.locator('#project-name')).toHaveValue(journey.projectName);
      await expect(page.locator('main')).toContainText(journey.projectId);
      await expect(page.locator('main').getByText('Regions', { exact: true })).toBeVisible();
      await expect(page.locator('main').getByText('Primary region')).toHaveCount(0);
      await expect(page.locator('main').getByText('Backup region')).toHaveCount(0);
    },
  },
  {
    name: 'project members',
    url: () => '/project/members',
    covers: { path: /^\/project\/members$/ },
    heading: region('Explicit project grants'),
    data: async (page, journey) => {
      await expect(page.locator('main p').first()).toContainText(`${journey.projectName} · your role: admin`);
      const grant = page.locator('main tr[data-project-grant-user]');
      await expect(grant).toHaveCount(1);
      await expect(grant).toContainText(journey.email);
    },
  },
  {
    name: 'project invitations',
    url: () => '/project/invitations',
    covers: { path: /^\/project\/invitations$/ },
    heading: title('Project invitations'),
    data: async (page, journey) => {
      await expect(page.locator('main')).toContainText(journey.projectName);
      await expect(page.getByText('No project invitations on this page.', { exact: true })).toBeVisible();
    },
  },
  {
    name: 'profile',
    url: () => '/profile',
    covers: { path: /^\/profile$/ },
    heading: title('Profile details', 2),
    data: async (page, journey) => {
      await expect(page.locator('h1.ts-profile-name')).toHaveText(journey.displayName);
      await expect(page.locator('#displayName')).toHaveValue(journey.displayName);
      await expect(page.locator('main')).toContainText(journey.email);
    },
  },
  ...profileTabs.map(
    ([tab, heading]): PageState => ({
      name: `profile ${tab}`,
      url: () => `/profile?tab=${tab}`,
      covers: { path: /^\/profile$/, query: { tab: new RegExp(`^${tab}$`) } },
      heading,
      data: async (page, journey) => {
        await expect(page.locator('h1.ts-profile-name')).toHaveText(journey.displayName);
        if (tab === 'profile') {
          await expect(page.locator('#displayName')).toHaveValue(journey.displayName);
          await expect(page.getByText('Sign in with email only.', { exact: true })).toBeVisible();
        } else if (tab === 'sessions') {
          const row = page.locator('#profile-sessions tr').filter({ hasText: 'This device' });
          await expect(row).toHaveCount(1);
          await expect(row.locator('td').nth(1)).not.toHaveText('');
        } else if (tab === 'access-tokens') {
          await expect(page.getByText('No access tokens', { exact: true })).toBeVisible();
        } else if (tab === 'danger') {
          await expect(page.locator('label[for="delete-account-email"]')).toContainText(journey.email);
        }
      },
    }),
  ),
  {
    name: 'notifications',
    url: () => '/notifications',
    covers: { path: /^\/notifications$/ },
    heading: title('Notifications'),
    data: async (page) => {
      await expect(page.getByText('No notifications in this scope.', { exact: true })).toBeVisible();
    },
  },
  {
    name: 'unread notifications',
    url: () => '/notifications?status=unread',
    covers: { path: /^\/notifications$/, query: { status: /^unread$/ } },
    heading: title('Notifications'),
    data: async (page) => {
      await expect(page.getByText('No unread notifications in this scope.', { exact: true })).toBeVisible();
    },
  },
  {
    name: 'alert rules',
    url: () => '/notifications/alerts',
    covers: { path: /^\/notifications\/alerts$/ },
    heading: title('Create alert rule', 2),
    data: async (page, journey) => {
      await expect(page.locator('#alert-rules-title')).toContainText(journey.projectName);
      await expect(page.getByText('No alert rules yet.', { exact: true })).toBeVisible();
    },
  },
  {
    name: 'support',
    url: () => '/support',
    covers: { path: /^\/support$/ },
    heading: title('Contact support'),
    data: async (page, journey) => {
      await expect(page.locator('#support-organization')).toHaveValue(journey.organizationId);
      await expect(page.locator('#support-project')).toHaveValue(journey.projectId);
      await expect(page.locator('#support-targets')).toContainText('Free');
    },
  },
  {
    name: 'support about a request',
    url: (journey) => `/support?relatedRequestId=${journey.databaseId}`,
    covers: { path: /^\/support$/, query: { relatedRequestId: id } },
    heading: title('Contact support'),
    data: async (page, journey) => {
      await expect(page.locator('#support-related-request')).toHaveValue(journey.databaseId);
      await expect(page.locator('#support-organization')).toHaveValue(journey.organizationId);
    },
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
    covers: { path: /^\/account-deletion$/ },
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
    data: async (page) => {
      await expect(page.locator('main')).toContainText('Journey MCP client');
    },
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
