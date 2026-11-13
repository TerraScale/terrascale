import { expect, test } from '../support/audit';
import { checkMachineClient, closeAuthManagement, prepareAuthManagement } from '../support/auth-management';
import { createProjectOwner } from '../support/project-owner';
import { auth } from '../support/stack';

test('machine credentials restrict scope and audience and interactive tokens identify users', async ({ browser }, info) => {
  const account = await createProjectOwner(browser, info);
  const { current, page } = account;
  try {
    const management = await prepareAuthManagement(page, 'machine', current);
    try {
      await checkMachineClient(page, management.ownerToken, current.projectId, current.password);
      for (const action of ['disable', 'enable']) {
        const response = await page.request.post(auth + '/v1/' + current.projectId + '/users/' + management.user + '/' + action, {
          headers: { Authorization: 'Bearer ' + management.writeKey },
        });
        expect(response.status()).toBe(204);
      }
    } finally {
      await closeAuthManagement(page, management, current);
    }
  } finally {
    await account.close();
  }
});
