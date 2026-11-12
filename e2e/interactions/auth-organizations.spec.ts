import { createHash, randomBytes } from 'node:crypto';
import type { Page } from '@playwright/test';
import { Audit, auditedContext, expect, test } from '../support/audit';
import { confirmWithPassword, hostedReady } from '../support/hosted';
import { newAddress, nextMail } from '../support/inbox';
import { enterSecret } from '../support/secret';
import { auth, dash, desktop } from '../support/stack';
import { journey, ownerSession, saveSecret } from '../support/state';
import type { TerraScaleResult } from '../../sdk/js/types/result';

const { createTsAuthClient } = require('../../sdk/js/src/ts-auth.js') as typeof import('../../sdk/js/types/ts-auth');

function value<T>(result: TerraScaleResult<T>): T {
  if (!result.ok) throw new Error('Organization SDK call failed: ' + result.error.code);
  return result.value;
}

test('project organizations bind invitations, roles and refresh to the chosen organization', async ({ browser }, info) => {
  const current = journey();
  const sdk = createTsAuthClient({ apiBaseUrl: auth, instanceSlug: 'terrascale' });
  const organizations = sdk.projects.organizations;
  const owner = await auditedContext(browser, { viewport: desktop, storageState: ownerSession() });
  const admin = await auditedContext(browser, { viewport: desktop, storageState: { cookies: [], origins: [] } });
  const invited = await auditedContext(browser, { viewport: desktop, storageState: { cookies: [], origins: [] } });
  const page = await invited.newPage();
  const adminPage = await admin.newPage();
  const ownerPage = await owner.newPage();
  const audit = new Audit(page, info);
  const ownerAudit = new Audit(ownerPage, info);
  const password = randomBytes(24).toString('base64url');
  saveSecret('organization-user-password', password);
  const adminEmail = await newAddress('org-admin-' + randomBytes(5).toString('hex'));
  const email = await newAddress('org-invited-' + randomBytes(5).toString('hex'));
  const key = current.apiKey;
  let org = '';
  let other = '';
  let client = '';
  const redirect = dash + '/auth/organizations';

  const signup = async (target: Page, address: string, seen: number, next = '') => {
    await target.goto(auth + '/ui/' + current.projectId + '/sign-up' + (next ? '?next=' + encodeURIComponent(next) : ''));
    await hostedReady(target);
    await target.locator('#sign-up-email').fill(address);
    await enterSecret(target.locator('#sign-up-password'), password);
    await target.locator('form:has(#sign-up-email) button[type=submit]').click();
    const mail = await nextMail(address, seen);
    const code = /^\s*(\d{6})\s*$/m.exec(mail.text)?.[1];
    expect(code).toMatch(/^\d{6}$/);
    await enterSecret(target.locator('#sign-up-code-digit'), code!);
    await target.getByRole('button', { name: 'Verify email' }).click();
  };

  const authorize = async (target: Page, organization?: string) => {
    const verifier = randomBytes(32).toString('base64url');
    const query = new URLSearchParams({
      client_id: client, redirect_uri: redirect, response_type: 'code', scope: 'openid email offline_access',
      code_challenge: createHash('sha256').update(verifier).digest('base64url'), code_challenge_method: 'S256', state: 'org-journey',
    });
    if (organization) query.set('organization', organization);
    const response = await target.request.get(auth + '/' + current.projectId + '/oauth/authorize?' + query, { maxRedirects: 0 });
    return { response, verifier };
  };

  const tokens = async (target: Page, organization: string) => {
    const { response, verifier } = await authorize(target, organization);
    expect(response.status()).toBe(302);
    const location = new URL(response.headers().location);
    expect(location.searchParams.get('error')).toBeNull();
    const responseToken = await target.request.post(auth + '/' + current.projectId + '/oauth/token', {
      form: { grant_type: 'authorization_code', code: location.searchParams.get('code')!, client_id: client, redirect_uri: redirect, code_verifier: verifier },
    });
    expect(responseToken.status()).toBe(200);
    const result = await responseToken.json() as { access_token: string; refresh_token: string; id_token: string };
    saveSecret('organization-access', result.access_token);
    saveSecret('organization-refresh', result.refresh_token);
    saveSecret('organization-id', result.id_token);
    return result;
  };

  const claims = (token: string) => JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString()) as { org_id: string; org_slug: string; org_role: string };

  try {
    await signup(adminPage, adminEmail, 0);
    await expect(adminPage.locator('[data-state=success]')).toContainText('signed in');
    const adminId = value(await sdk.projects.listUsers(key, current.projectId, { search: adminEmail })).items[0].id;
    const input = { name: 'Journey organization', slug: 'org-' + randomBytes(5).toString('hex'), admin_user_id: adminId,
      image_url: null, public_metadata: { tier: 'journey' }, private_metadata: { internal: 'private' }, max_members: 3, domain_join: 'off' };
    org = value(await organizations.createOrganization(key, current.projectId, input)).id;
    expect(value(await organizations.getOrganization(key, current.projectId, org)).public_metadata).toEqual({ tier: 'journey' });
    expect(value(await organizations.listOrganizations(key, current.projectId)).items.some(item => item.id === org)).toBe(true);

    const removed = await organizations.deleteOrganizationMember(key, current.projectId, org, adminId);
    expect(removed.ok).toBe(false);
    if (!removed.ok) expect(removed.status).toBe(409);
    const demoted = await organizations.updateOrganizationMember(key, current.projectId, org, adminId, { user_id: null, role: 'member' });
    expect(demoted.ok).toBe(false);
    if (!demoted.ok) expect(demoted.status).toBe(409);

    value(await organizations.createOrganizationInvitation(key, current.projectId, org, { email, role: 'member' }));
    const invitationMail = await nextMail(email, 0);
    const invitationUrl = /https:\/\/[^\s<>]+\/organization-invitations\?token=[A-Za-z0-9]{48}/.exec(invitationMail.text)?.[0];
    expect(invitationUrl).toBeTruthy();
    saveSecret('organization-invitation', invitationUrl!);
    const invitationLink = new URL(invitationUrl!);
    const invitationPath = invitationLink.pathname + invitationLink.search;
    const wrongEmail = await adminPage.request.post(auth + invitationLink.pathname, { headers: { Origin: auth }, form: { token: invitationLink.searchParams.get("token")! } });
    expect(wrongEmail.status()).toBe(403);
    const signedOut = await page.request.get(invitationUrl!, { maxRedirects: 0 });
    expect(signedOut.status()).toBe(302);
    expect(signedOut.headers().location).toContain('/sign-in?next=');
    await signup(page, email, 1, invitationPath);
    await expect(page.getByRole('button', { name: 'Accept invitation' })).toBeVisible();
    await expect(page.locator('main')).toContainText(email);
    await audit.check('organization invitation');
    await page.getByRole('button', { name: 'Accept invitation' }).click();
    await expect(page.getByRole('status')).toHaveText('Invitation accepted');
    const replay = await page.request.post(auth + invitationLink.pathname, { headers: { Origin: auth }, form: { token: invitationLink.searchParams.get("token")! } });
    expect(replay.status()).toBe(404);
    const invitedId = value(await sdk.projects.listUsers(key, current.projectId, { search: email })).items[0].id;
    expect(value(await organizations.listOrganizationMembers(key, current.projectId, org)).items.find(member => member.user_id === invitedId)?.role).toBe('member');
    expect(value(await organizations.listOrganizationInvitations(key, current.projectId, org)).items[0].status).toBe('accepted');
    other = value(await organizations.createOrganization(key, current.projectId, { ...input, name: 'Second organization', slug: input.slug + '-two', admin_user_id: invitedId })).id;

    await ownerPage.goto(dash + '/auth/clients');
    await ownerPage.getByRole('button', { name: 'Add application', exact: true }).first().click();
    await ownerPage.locator('#auth-client-name').fill('Organization journey');
    await ownerPage.locator('#auth-client-type').selectOption('public');
    await ownerPage.locator('#auth-client-uris').fill(redirect);
    await ownerPage.locator('#auth-create-client button[type=submit]').click();
    await confirmWithPassword(ownerPage, ownerAudit, current.password, 'organization OAuth client');
    client = await ownerPage.locator('#auth-created-client-id').innerText();

    const selected = await tokens(page, org);
    expect(claims(selected.access_token)).toMatchObject({ org_id: org, org_slug: input.slug, org_role: 'member' });
    expect(claims(selected.id_token)).toMatchObject({ org_id: org, org_role: 'member' });
    expect(claims((await tokens(page, other)).access_token)).toMatchObject({ org_id: other, org_role: 'admin' });
    const forbidden = await authorize(adminPage, other);
    expect(new URL(forbidden.response.headers().location).searchParams.get('error')).toBe('access_denied');
    const chooser = await authorize(page);
    expect(chooser.response.status()).toBe(200);
    expect(await chooser.response.text()).toContain(input.slug + '-two');
    const refresh = await page.request.post(auth + '/' + current.projectId + '/oauth/token', {
      form: { grant_type: 'refresh_token', refresh_token: selected.refresh_token, client_id: client },
    });
    expect(refresh.status()).toBe(200);
    const refreshed = await refresh.json();
    saveSecret('organization-refreshed', refreshed.access_token);
    saveSecret('organization-refreshed-refresh', refreshed.refresh_token);
    expect(claims(refreshed.access_token)).toMatchObject({ org_id: org, org_role: 'member' });
    value(await organizations.deleteOrganization(key, current.projectId, org));
    org = '';
    const revoked = await page.request.post(auth + '/' + current.projectId + '/oauth/token', {
      form: { grant_type: 'refresh_token', refresh_token: refreshed.refresh_token, client_id: client },
    });
    expect(revoked.status()).toBe(400);
    expect((await revoked.json()).error).toBe('invalid_grant');
    await audit.finish();
  } finally {
    if (org) value(await organizations.deleteOrganization(key, current.projectId, org));
    if (other) value(await organizations.deleteOrganization(key, current.projectId, other));
    if (client) value(await sdk.projects.deleteClient(key, current.projectId, client));
    await Promise.all([owner.close(), admin.close(), invited.close()]);
  }
});
