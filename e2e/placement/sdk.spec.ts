import { test } from './support';
import { runOperations, sdks } from '../support/sdk';
import { production } from '../support/stack';

test.skip(production || process.env.E2E_REGIONS !== '2', 'SDK placement requires E2E_REGIONS=2');
test.describe.configure({ mode: 'serial', timeout: 180_000 });

for (const sdk of sdks) {
  test(`${sdk} SDK placement through region sets`, async ({ cells }) => {
    void cells;
    await runOperations(sdk, 'b');
  });
}
