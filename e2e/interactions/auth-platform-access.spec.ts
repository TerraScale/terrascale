import { randomBytes } from 'node:crypto';
import type { APIRequestContext } from '@playwright/test';
import { expect, test } from '../support/audit';
import { signInToken, stepUp } from '../support/auth-management';
import { request } from '../support/client-address';
import { newAddress, nextMail } from '../support/inbox';
import { createDedicatedProjectOwner } from '../support/project-owner';
import { auth } from '../support/stack';
import { saveSecret } from '../support/state';

const ui = auth + '/ui/terrascale';
const origin = { Origin: auth };

async function signUpMember(member: APIRequestContext, email: string, password: string): Promise<void> {
  const started = await member.post(ui + '/sign-up', { headers: origin, form: { email, password } });
  expect(started.status()).toBe(200);
  const pending = /name="pending_token" value="([^"]+)"/.exec(await started.text())?.[1];
  expect(pending).toBeTruthy();
  saveSecret('platform-access-member-pending', pending!);
  const code = /^\s*(\d{6})\s*$/m.exec((await nextMail(email, 0)).text)?.[1];
  expect(code).toBeTruthy();
  const verified = await member.post(ui + '/sign-up/verify', { headers: origin, form: { email, code: code!, pending_token: pending! } });
  expect(verified.status()).toBe(200);
}

test('a member\'s API keys survive its own sign-out everywhere, but the keys and invitations its new role no longer covers end when it is demoted', async ({ browser }, info) => {
  const account = await createDedicatedProjectOwner(browser, info, 'Platform access owner');
  const { current, page } = account;
  const member = await request.newContext({ storageState: { cookies: [], origins: [] } });
  const memberEmail = await newAddress('platform-access-member-' + current.runId);
  const inviteeEmail = await newAddress('platform-access-invitee-' + current.runId);
  const memberPassword = randomBytes(24).toString('base64url');
  saveSecret('platform-access-member-password', memberPassword);
  const organization = auth + '/v1/terrascale/organizations/' + current.organizationId;
  const projectKeys = organization + '/projects/' + current.projectId + '/api-keys';
  const users = auth + '/v1/' + current.projectId + '/users';
  try {
    const ownerToken = await signInToken(page.request, 'platform-access-owner');
    const owner = { Authorization: 'Bearer ' + ownerToken };

    for (const name of ['TerraScale Support', 'Terra Scale billing', 'Terrasca1e team', 'Acme <admin>', 'ops@acme', 'Quote " team', 'bad‮name', 'zero​width']) {
      const refused = await page.request.post(auth + '/v1/terrascale/organizations', { headers: owner, data: { name } });
      expect(refused.status(), 'organization name ' + JSON.stringify(name)).toBe(400);
      expect((await refused.json()).error.code).toBe('invalid_request');
    }

    for (const limit of ['0', '101']) {
      expect((await page.request.get(auth + '/v1/' + current.projectId + '/clients?limit=' + limit, { headers: owner })).status()).toBe(400);
    }
    const clients = await page.request.get(auth + '/v1/' + current.projectId + '/clients?limit=1', { headers: owner });
    expect(clients.status()).toBe(200);
    expect(((await clients.json()).items as unknown[]).length).toBeLessThanOrEqual(1);

    await signUpMember(member, memberEmail, memberPassword);
    let memberToken = await signInToken(member, 'platform-access-member');
    const invited = await page.request.post(organization + '/invitations', { headers: owner, data: { email: memberEmail, role: 'admin' } });
    expect(invited.status()).toBe(201);
    const invitation = (await invited.json()).token as string;
    saveSecret('platform-access-member-invitation', invitation);
    expect((await member.post(auth + '/v1/terrascale/invitations/accept', {
      headers: { Authorization: 'Bearer ' + memberToken }, data: { token: invitation },
    })).status()).toBe(200);
    const members = await page.request.get(organization + '/members', { headers: owner });
    expect(members.status()).toBe(200);
    const memberId = ((await members.json()).items as { user_id: string; email: string }[]).find(item => item.email === memberEmail)!.user_id;

    const created = await member.post(projectKeys, {
      headers: { Authorization: 'Bearer ' + memberToken }, data: { name: 'member admin key', scopes: ['auth:read', 'auth:write'] },
    });
    expect(created.status()).toBe(201);
    const memberKey = (await created.json()).key as string;
    saveSecret('platform-access-member-key', memberKey);
    const keyHeaders = { Authorization: 'Bearer ' + memberKey };
    expect((await member.get(users, { headers: keyHeaders })).status()).toBe(200);

    const pending = await member.post(organization + '/invitations', {
      headers: { Authorization: 'Bearer ' + memberToken }, data: { email: inviteeEmail, role: 'member' },
    });
    expect(pending.status()).toBe(201);
    const pendingId = (await pending.json()).invitation.id as string;

    expect((await member.post(auth + '/v1/terrascale/account/sign-out', { headers: { Authorization: 'Bearer ' + memberToken } })).status()).toBe(204);
    expect((await member.get(auth + '/v1/terrascale/organizations', { headers: { Authorization: 'Bearer ' + memberToken } })).status()).toBe(401);
    expect((await member.get(users, { headers: keyHeaders })).status(), 'the member\'s key after its own sign-out everywhere').toBe(200);

    const demoted = await page.request.put(organization + '/members/' + memberId + '/role', { headers: owner, data: { role: 'member' } });
    expect(demoted.status()).toBe(200);
    expect((await member.get(users, { headers: keyHeaders })).status(), 'the member\'s auth:write key after its demotion to member').toBe(401);
    const keys = await page.request.get(projectKeys + '?limit=100', { headers: owner });
    expect(keys.status()).toBe(200);
    expect(((await keys.json()).items as { id: string }[]).some(item => item.id === memberKey.slice(0, 26))).toBe(false);
    const invitations = await page.request.get(organization + '/invitations?limit=100', { headers: owner });
    expect(invitations.status()).toBe(200);
    expect(((await invitations.json()).items as { id: string; status: string }[]).find(item => item.id === pendingId)?.status).toBe('revoked');

    expect((await page.request.delete(organization + '/members/' + memberId, { headers: owner })).status()).toBe(204);
    expect((await member.post(ui + '/sign-in/password', { headers: origin, form: { email: memberEmail, password: memberPassword } })).status()).toBe(200);
    memberToken = await signInToken(member, 'platform-access-member-cleanup');
    const proof = await stepUp(member, memberToken, 'delete_account', memberPassword);
    expect((await member.delete(auth + '/v1/terrascale/account', {
      headers: { Authorization: 'Bearer ' + memberToken, 'X-TerraScale-Step-Up-Proof': proof },
    })).status()).toBe(204);
  } finally {
    await member.dispose();
    await account.remove();
  }
});
