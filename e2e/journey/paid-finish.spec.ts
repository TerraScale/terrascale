import { test, expect } from '../support/audit';
import { runOperations, sdks } from '../support/sdk';
import { setupFinished } from '../support/state';

test.describe.configure({ mode: 'default' });

test.beforeAll(({}, info) => {
  expect(setupFinished(info), 'setup did not finish').toBe(true);
});

test.describe('SDK group C', () => {
  for (const sdk of sdks) {
    test(`${sdk} SDK: a fork holds its source's rows and a scoped key is refused on another database of its project`, async () => {
      await runOperations(sdk, 'c');
    });
  }
});
