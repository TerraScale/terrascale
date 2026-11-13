import { createHash, randomBytes } from 'node:crypto';
import type { Page } from '@playwright/test';
import { Audit, auditedContext, expect, test } from '../support/audit';
import { createProjectOwner } from '../support/project-owner';
import { signInToken } from '../support/auth-management';
import { confirmWithPassword, hostedReady } from '../support/hosted';
import { newAddress, nextMail } from '../support/inbox';
import { enterSecret } from '../support/secret';
import { auth, dash, desktop, onLocalStack } from '../support/stack';
import { saveSecret } from '../support/state';
import type { TerraScaleResult } from '../../sdk/js/src/result.js';

const { createTsAuthClient } = require('../../sdk/js/src/ts-auth.js') as typeof import('../../sdk/js/src/ts-auth.js');

function value<T>(result: TerraScaleResult<T>): T {
  if (!result.ok) throw new Error('Organization SDK call failed: ' + result.error.code);
  return result.value;
}

test('project organizations bind invitations, roles and refresh to the chosen organization', async ({ browser }, info) => {
  const account = await createProjectOwner(browser, info);
  const { current } = account;
  const sdk = createTsAuthClient({ apiBaseUrl: auth, instanceSlug: 'terrascale' });
  const organizations = sdk.projects.organizations;
  const admin = await auditedContext(browser, { viewport: desktop, storageState: { cookies: [], origins: [] } });
  const invited = await auditedContext(browser, { viewport: desktop, storageState: { cookies: [], origins: [] } });
  const outsider = await auditedContext(browser, { viewport: desktop, storageState: { cookies: [], origins: [] } });
  const outsiderPage = await outsider.newPage();
  const page = await invited.newPage();
  const adminPage = await admin.newPage();
  const ownerPage = account.page;
  const audit = new Audit(page, info);
  const ownerAudit = account.audit;
  const password = randomBytes(24).toString('base64url');
  saveSecret('organization-user-password', password);
  const adminEmail = await newAddress('org-admin-' + randomBytes(5).toString('hex'));
  const email = await newAddress('org-invited-' + randomBytes(5).toString('hex'));
  const key = current.apiKey;
  let org = '';
  let other = '';
  let client = '';
  const extraOrganizations: string[] = [];
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

    const ownerToken = await signInToken(ownerPage.request, 'organization-owner');
    const invitation = value(await organizations.createOrganizationInvitation(ownerToken, current.projectId, org, { email, role: 'member' }));
    expect(Date.parse(invitation.expires_at) - Date.parse(invitation.created_at)).toBe(7 * 24 * 60 * 60 * 1000);
    const invitationMail = await nextMail(email, 0);
    expect(invitationMail.text).toContain(input.name + ' invited');
    await onLocalStack(async () => expect(invitationMail.fromName).toMatch(/^Journey organization \(.+\)$/));
    expect(invitationMail.text).not.toContain(current.email);
    for (const inviter_name of ['', 'x'.repeat(101), 'bad\nname', 'bad\u202ename', '<b>name</b>']) {
      const invalid = await organizations.createOrganizationInvitation(key, current.projectId, org, { email, role: 'member', inviter_name });
      expect(invalid.ok).toBe(false);
      if (!invalid.ok) expect(invalid.status).toBe(400);
    }
    const named = value(await organizations.createOrganizationInvitation(key, current.projectId, org, {
      email: adminEmail, role: 'member', inviter_name: 'Customer teammate',
    }));
    const namedMail = await nextMail(adminEmail, 1);
    expect(namedMail.text).toContain('Customer teammate invited');
    expect(namedMail.text).not.toContain(current.email);
    value(await organizations.revokeOrganizationInvitation(key, current.projectId, org, named.id));
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
    const acceptance = page.waitForResponse(response => response.request().method() === "POST" && new URL(response.url()).pathname === invitationLink.pathname);
    await page.getByRole('button', { name: 'Accept invitation' }).click();
    expect((await acceptance).status()).toBe(200);
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

    const foreignProjectResponse = await ownerPage.request.post(auth + '/v1/terrascale/organizations/' + current.organizationId + '/projects', {
      headers: { Authorization: 'Bearer ' + ownerToken }, data: { name: 'Organization isolation ' + randomBytes(5).toString('hex') },
    });
    expect(foreignProjectResponse.status()).toBe(201);
    const foreignProject = (await foreignProjectResponse.json()).id as string;
    const foreignEmail = await newAddress('org-foreign-' + randomBytes(5).toString('hex'));
    await adminPage.goto(auth + '/ui/' + foreignProject + '/sign-up');
    await hostedReady(adminPage);
    await adminPage.locator('#sign-up-email').fill(foreignEmail);
    await enterSecret(adminPage.locator('#sign-up-password'), password);
    await adminPage.locator('form:has(#sign-up-email) button[type=submit]').click();
    const foreignMail = await nextMail(foreignEmail, 0);
    const foreignCode = /^\s*(\d{6})\s*$/m.exec(foreignMail.text)?.[1];
    expect(foreignCode).toBeTruthy();
    await enterSecret(adminPage.locator('#sign-up-code-digit'), foreignCode!);
    await adminPage.getByRole('button', { name: 'Verify email' }).click();
    await expect(adminPage.locator('[data-state=success]')).toContainText('signed in');
    const foreignAdmin = value(await sdk.projects.listUsers(ownerToken, foreignProject, { search: foreignEmail })).items[0].id;
    const foreignOrg = value(await organizations.createOrganization(ownerToken, foreignProject, { ...input, admin_user_id: foreignAdmin })).id;
    const wrongProject = await organizations.getOrganization(key, current.projectId, foreignOrg);
    expect(wrongProject.ok).toBe(false);
    if (!wrongProject.ok) expect(wrongProject.status).toBe(404);
    value(await organizations.deleteOrganization(ownerToken, foreignProject, foreignOrg));
    const deletedProject = await ownerPage.request.delete(auth + '/v1/terrascale/organizations/' + current.organizationId + '/projects/' + foreignProject, {
      headers: { Authorization: 'Bearer ' + ownerToken },
    });
    expect(deletedProject.status()).toBe(204);

    const outsiderEmail = await newAddress('org-outsider-' + randomBytes(5).toString('hex'));
    await signup(outsiderPage, outsiderEmail, 0);
    await expect(outsiderPage.locator('[data-state=success]')).toContainText('signed in');
    const outsiderId = value(await sdk.projects.listUsers(key, current.projectId, { search: outsiderEmail })).items[0].id;
    const outsiderDenied = await authorize(outsiderPage, org);
    expect(new URL(outsiderDenied.response.headers().location).searchParams.get('error')).toBe('access_denied');

    await onLocalStack(async () => {
      const domain = outsiderEmail.split('@')[1];
      const firstClaim = value(await organizations.addOrganizationDomain(key, current.projectId, org, { domain }));
      const secondClaim = value(await organizations.addOrganizationDomain(key, current.projectId, other, { domain }));
      const published = await outsiderPage.request.post('http://127.0.0.1:8032/records', {
        data: { name: firstClaim.record_name, value: firstClaim.record_value },
      });
      expect(published.status()).toBe(200);
      expect((await outsiderPage.request.post('http://127.0.0.1:8032/records', {
        data: { name: firstClaim.record_name, value: firstClaim.record_value, status: 2 },
      })).status()).toBe(200);
      const unavailable = await organizations.verifyOrganizationDomain(key, current.projectId, org, domain);
      expect(unavailable.ok).toBe(false);
      if (!unavailable.ok) {
        expect(unavailable.status).toBe(503);
        expect(unavailable.error.code).toBe('dns_unavailable');
      }
      expect((await outsiderPage.request.post('http://127.0.0.1:8032/records', {
        data: { name: firstClaim.record_name, value: firstClaim.record_value },
      })).status()).toBe(200);
      expect(value(await organizations.verifyOrganizationDomain(key, current.projectId, org, domain)).verified_at).toBeTruthy();
      expect((await outsiderPage.request.post('http://127.0.0.1:8032/records', {
        data: { name: secondClaim.record_name, value: secondClaim.record_value },
      })).status()).toBe(200);
      const competing = await organizations.verifyOrganizationDomain(key, current.projectId, other, domain);
      expect(competing.ok).toBe(false);
      if (!competing.ok) expect(competing.status).toBe(409);
      value(await organizations.updateOrganization(key, current.projectId, org, { ...input, domain_join: 'suggest' }));
      const suggestion = await authorize(outsiderPage);
      expect(suggestion.response.status()).toBe(200);
      expect(await suggestion.response.text()).toContain('Continue without joining');
      await outsiderPage.goto(suggestion.response.url());
      await expect(outsiderPage.getByRole('button', { name: 'Continue without joining' })).toBeVisible();
      await expect(outsiderPage.getByRole('button', { name: 'Join ' + input.slug, exact: true })).toBeVisible();
      await new Audit(outsiderPage, info).check('suggested organization chooser');
      const next = new URL(suggestion.response.url()).pathname + new URL(suggestion.response.url()).search;
      const dismissed = await outsiderPage.request.post(auth + '/ui/' + current.projectId + '/organizations/' + org + '/join', {
        headers: { Origin: auth }, form: { next, dismiss: 'true' }, maxRedirects: 0,
      });
      expect(dismissed.status()).toBe(302);
      const resumed = await outsiderPage.request.get(auth + dismissed.headers().location, { maxRedirects: 0 });
      expect(resumed.status()).toBe(302);
      expect(new URL(resumed.headers().location).searchParams.get('error')).toBeNull();
      const stillDismissed = await authorize(outsiderPage);
      expect(stillDismissed.response.status()).toBe(302);
      expect(value(await organizations.listOrganizationMembers(key, current.projectId, org)).items.some(member => member.user_id === outsiderId)).toBe(false);

      value(await organizations.updateOrganization(key, current.projectId, org, { ...input, max_members: 2, domain_join: 'auto' }));
      const full = await authorize(outsiderPage);
      expect(full.response.status()).toBe(302);
      expect(new URL(full.response.headers().location).searchParams.get('error')).toBeNull();
      expect(value(await organizations.listOrganizationMembers(key, current.projectId, org)).items.some(member => member.user_id === outsiderId)).toBe(false);
      value(await organizations.updateOrganization(key, current.projectId, org, { ...input, domain_join: 'auto' }));
      const joined = await tokens(outsiderPage, org);
      expect(claims(joined.access_token)).toMatchObject({ org_id: org, org_role: 'member' });
      value(await organizations.updateOrganization(key, current.projectId, org, { ...input, domain_join: 'suggest' }));
      value(await organizations.deleteOrganizationMember(key, current.projectId, org, outsiderId));
      expect((await authorize(outsiderPage)).response.status()).toBe(200);
      value(await organizations.addOrganizationMember(key, current.projectId, org, { user_id: outsiderId, role: 'member' }));
      value(await organizations.deleteOrganizationDomain(key, current.projectId, org, domain));
    });
    if (!value(await organizations.listOrganizationMembers(key, current.projectId, org)).items.some(member => member.user_id === outsiderId)) {
      value(await organizations.addOrganizationMember(key, current.projectId, org, { user_id: outsiderId, role: 'member' }));
    }
    expect(value(await organizations.listOrganizationMembers(key, current.projectId, org)).items.some(member => member.user_id === outsiderId)).toBe(true);

    const selected = await tokens(page, org);
    expect(claims(selected.access_token)).toMatchObject({ org_id: org, org_slug: input.slug, org_role: 'member' });
    expect(claims(selected.id_token)).toMatchObject({ org_id: org, org_role: 'member' });
    expect(claims((await tokens(page, other)).access_token)).toMatchObject({ org_id: other, org_role: 'admin' });
    const forbidden = await authorize(adminPage, other);
    expect(new URL(forbidden.response.headers().location).searchParams.get('error')).toBe('access_denied');
    const chooser = await authorize(page);
    expect(chooser.response.status()).toBe(200);
    expect(await chooser.response.text()).toContain(input.slug + '-two');
    value(await organizations.updateOrganizationMember(key, current.projectId, org, invitedId, { user_id: null, role: "admin" }));
    const refresh = await page.request.post(auth + '/' + current.projectId + '/oauth/token', {
      form: { grant_type: 'refresh_token', refresh_token: selected.refresh_token, client_id: client },
    });
    expect(refresh.status()).toBe(200);
    const refreshed = await refresh.json();
    saveSecret('organization-refreshed', refreshed.access_token);
    saveSecret('organization-refreshed-refresh', refreshed.refresh_token);
    expect(claims(refreshed.access_token)).toMatchObject({ org_id: org, org_role: 'admin' });
    const pendingInvitation = value(await organizations.createOrganizationInvitation(key, current.projectId, org, { email: adminEmail, role: 'member' }));
    const deletedOrg = org;
    const pendingMail = await nextMail(adminEmail, 2);
    const pendingLink = /https:\/\/[^\s<>]+\/organization-invitations\?token=[A-Za-z0-9]{48}/.exec(pendingMail.text)?.[0];
    expect(pendingLink).toBeTruthy();
    saveSecret('organization-revoked-invitation', pendingLink!);
    value(await organizations.deleteOrganization(key, current.projectId, org));
    org = '';
    expect((await adminPage.request.get(pendingLink!, { maxRedirects: 0 })).status()).toBe(404);
    const revoked = await page.request.post(auth + '/' + current.projectId + '/oauth/token', {
      form: { grant_type: 'refresh_token', refresh_token: refreshed.refresh_token, client_id: client },
    });
    expect(revoked.status()).toBe(400);
    expect((await revoked.json()).error).toBe('invalid_grant');
    const succession = value(await organizations.createOrganization(key, current.projectId, { ...input, slug: input.slug + '-succession' })).id;
    extraOrganizations.push(succession);
    value(await organizations.addOrganizationMember(key, current.projectId, succession, { user_id: invitedId, role: 'member' }));
    value(await organizations.addOrganizationMember(key, current.projectId, succession, { user_id: outsiderId, role: 'member' }));
    const beforePromotion = value(await organizations.listOrganizationMembers(key, current.projectId, succession)).items;
    expect(Date.parse(beforePromotion.find(member => member.user_id === invitedId)!.created_at))
      .toBeLessThan(Date.parse(beforePromotion.find(member => member.user_id === outsiderId)!.created_at));
    const sole = value(await organizations.createOrganization(key, current.projectId, { ...input, slug: input.slug + '-sole' })).id;
    extraOrganizations.push(sole);
    await onLocalStack(async () => {
      const domain = outsiderEmail.split('@')[1];
      value(await organizations.updateOrganization(key, current.projectId, sole, { ...input, slug: input.slug + '-sole', domain_join: 'auto' }));
      const claim = value(await organizations.addOrganizationDomain(key, current.projectId, sole, { domain }));
      expect((await outsiderPage.request.post('http://127.0.0.1:8032/records', {
        data: { name: claim.record_name, value: claim.record_value },
      })).status()).toBe(200);
      value(await organizations.verifyOrganizationDomain(key, current.projectId, sole, domain));
    });
    value(await sdk.projects.deleteUser(key, current.projectId, adminId));
    expect(value(await organizations.listOrganizationMembers(key, current.projectId, succession)).items).toEqual(expect.arrayContaining([
      expect.objectContaining({ user_id: invitedId, role: 'admin' }),
      expect.objectContaining({ user_id: outsiderId, role: 'member' }),
    ]));
    expect(value(await organizations.listOrganizationMembers(key, current.projectId, succession)).items).toHaveLength(2);
    const promotionInput = { ...input, slug: input.slug + '-succession', max_members: 2 };
    value(await organizations.updateOrganization(key, current.projectId, succession, promotionInput));
    const tooSmall = await organizations.updateOrganization(key, current.projectId, succession, { ...promotionInput, max_members: 1 });
    expect(tooSmall.ok).toBe(false);
    if (!tooSmall.ok) expect(tooSmall.status).toBe(400);
    const lastAdmin = await organizations.deleteOrganizationMember(key, current.projectId, succession, invitedId);
    expect(lastAdmin.ok).toBe(false);
    if (!lastAdmin.ok) expect(lastAdmin.status).toBe(409);
    value(await sdk.projects.deleteUser(key, current.projectId, outsiderId));
    expect(value(await organizations.listOrganizationMembers(key, current.projectId, succession)).items)
      .toMatchObject([{ user_id: invitedId, role: 'admin' }]);
    expect(value(await organizations.listOrganizationMembers(key, current.projectId, sole)).items).toEqual([]);
    value(await sdk.projects.deleteUser(key, current.projectId, invitedId));
    expect(value(await organizations.listOrganizationMembers(key, current.projectId, succession)).items).toEqual([]);
    expect(value(await organizations.listOrganizationMembers(key, current.projectId, other)).items).toEqual([]);
    await onLocalStack(async () => {
      const emptyEmail = await newAddress('org-empty-' + randomBytes(5).toString('hex'));
      await signup(outsiderPage, emptyEmail, 0);
      await expect(outsiderPage.locator('[data-state=success]')).toContainText('signed in');
      const emptyUser = value(await sdk.projects.listUsers(key, current.projectId, { search: emptyEmail })).items[0].id;
      const emptyMember = await organizations.addOrganizationMember(key, current.projectId, sole, { user_id: emptyUser, role: 'member' });
      expect(emptyMember.ok).toBe(false);
      if (!emptyMember.ok) expect(emptyMember.status).toBe(409);
      const emptyJoin = await authorize(outsiderPage);
      expect(emptyJoin.response.status()).toBe(302);
      expect(new URL(emptyJoin.response.headers().location).searchParams.get('error')).toBeNull();
      expect(value(await organizations.listOrganizationMembers(key, current.projectId, sole)).items).toEqual([]);
    });
    const expectedEvents = [
      'organizationMembership.deleted:' + deletedOrg + ':' + outsiderId + ':member',
      'organizationMembership.deleted:' + deletedOrg + ':' + adminId + ':admin',
      'organizationMembership.deleted:' + deletedOrg + ':' + invitedId + ':admin',
      'organizationInvitation.revoked:' + deletedOrg + ':' + pendingInvitation.id + ':',
      'organizationInvitation.revoked:' + deletedOrg + ':' + invitation.id + ':',
      'organizationMembership.updated:' + succession + ':' + invitedId + ':admin',
      'organizationMembership.deleted:' + sole + ':' + adminId + ':admin',
    ];
    await expect.poll(async () => {
      const events: string[] = [];
      let after = '';
      do {
        const response = await ownerPage.request.get(auth + '/v1/' + current.projectId + '/events', {
          headers: { Authorization: 'Bearer ' + key }, params: { limit: 100, after },
        });
        expect(response.status()).toBe(200);
        const body = await response.json() as { items: { type: string; data: { organization_id?: string; user_id: string; organization_role?: string } }[]; next?: string };
        events.push(...body.items.map(event => [event.type, event.data.organization_id ?? '', event.data.user_id, event.data.organization_role ?? ''].join(':')));
        after = body.next ?? '';
      } while (after);
      return events;
    }).toEqual(expect.arrayContaining(expectedEvents));
    await audit.finish();
  } finally {
    if (org) value(await organizations.deleteOrganization(key, current.projectId, org));
    if (other) value(await organizations.deleteOrganization(key, current.projectId, other));
    for (const extra of extraOrganizations) value(await organizations.deleteOrganization(key, current.projectId, extra));
    if (client) value(await sdk.projects.deleteClient(key, current.projectId, client));
    await Promise.all([admin.close(), invited.close(), outsider.close()]);
    await account.close();
  }
});
