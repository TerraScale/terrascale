import { test } from '@playwright/test';
import { runOperations, sdks } from '../support/sdk';

test.describe.configure({ mode: 'default' });

for (const sdk of sdks) {
  test(`${sdk} SDK: SQL, values, sessions, conflicts and ids on the journey database`, async () => {
    await runOperations(sdk, 'a');
  });
}
