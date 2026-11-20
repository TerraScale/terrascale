import { expect, test } from '../support/audit';
import { checkMachineClient, closeAuthManagement, prepareAuthManagement, withCleanup } from '../support/auth-management';
import { createProjectOwner } from '../support/project-owner';
import { auth } from '../support/stack';
import { journey } from '../support/state';
import { checkTenantTokens } from '../support/tenant-tokens';

test('machine credentials restrict scope and audience, interactive tokens identify users and reach TerraBase only with its data audience', async ({ browser }, info) => {
  const account = await createProjectOwner(browser, info);
  const { current, page } = account;
  try {
    const management = await prepareAuthManagement(page, 'machine', current);
    await withCleanup(async () => {
      const tokens = await checkMachineClient(page, management.ownerToken, current.projectId, current.password);
      await checkTenantTokens(page.request, current.projectId, journey().projectId, current.apiKey, tokens);
      for (const action of ['disable', 'enable']) {
        const response = await page.request.post(auth + '/v1/' + current.projectId + '/users/' + management.user + '/' + action, {
          headers: { Authorization: 'Bearer ' + management.writeKey },
        });
        expect(response.status()).toBe(200);
      }
    }, () => closeAuthManagement(page, management, current));
  } finally {
    await account.close();
  }
});
