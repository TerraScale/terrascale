import { test } from '../support/audit';
import { checkMachineClient, closeAuthManagement, prepareAuthManagement } from '../support/auth-management';
import { createProjectOwner } from '../support/project-owner';

test('machine credentials restrict scope and audience and interactive tokens identify users', async ({ browser }, info) => {
  const account = await createProjectOwner(browser, info, 'Machine credentials owner');
  const { current, page } = account;
  try {
    const management = await prepareAuthManagement(page, 'machine', current);
    try {
      await checkMachineClient(page, management.ownerToken, current.projectId, current.password);
    } finally {
      await closeAuthManagement(page, management, current);
    }
  } finally {
    await account.close();
  }
});
