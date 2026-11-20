import { expect, test } from '../support/audit';
import { authStep, withAuthCleanup } from '../support/auth-failures';
import { checkMachineClient, closeAuthManagement, prepareAuthManagement } from '../support/auth-management';
import { createProjectOwner } from '../support/project-owner';
import { auth } from '../support/stack';
import { journey } from '../support/state';
import { checkTenantTokens } from '../support/tenant-tokens';

test('machine credentials restrict scope and audience, interactive tokens identify users and reach TerraBase only with its data audience', async ({ browser }, info) => {
  const account = await createProjectOwner(browser, info);
  const { current, page } = account;
  await withAuthCleanup('prepare-auth-management', async () => {
    const management = await prepareAuthManagement(page, 'machine', current);
    await withAuthCleanup('record-user-audit-actions', async () => {
      const tokens = await authStep('validate-machine-credentials', () => checkMachineClient(page, management.ownerToken, current.projectId, current.password));
      await authStep('validate-tenant-tokens', () => checkTenantTokens(page.request, current.projectId, journey().projectId, current.apiKey, tokens));
      for (const action of ['disable', 'enable']) {
        const response = await page.request.post(auth + '/v1/' + current.projectId + '/users/' + management.user + '/' + action, {
          headers: { Authorization: 'Bearer ' + management.writeKey },
        });
        expect(response.status()).toBe(200);
      }
    }, 'assert-auth-management-audit', () => closeAuthManagement(page, management, current));
  }, 'close-owner-context', account.close);
});
