import { afterAll, test } from "vitest";
import { generateId } from "./ids.js";
import {
  authFlowTestSuite,
  caseInsensitiveTestSuite,
  joinsTestSuite,
  normalTestSuite,
  testAdapter,
  transactionsTestSuite,
} from "@better-auth/test-utils/adapter";
import {
  localFixture,
  localSqliteUnitTestsAllowed,
} from "./local/test-environment.js";

if (!localSqliteUnitTestsAllowed) {
  test.skip("maintained Better Auth SQLite development suites require a permitted local environment", () =>
    undefined);
} else {
  const fixture = await localFixture();
  afterAll(() => fixture.close());
  const factory = await fixture.runtime.createBetterAuthAdapter();
  const suite = await testAdapter({
    adapter: () => factory,
    customIdGenerator: generateId,
    runMigrations: async (options) => {
      await factory(options).count({ model: "user" });
    },
    tests: [
      normalTestSuite(),
      transactionsTestSuite(),
      authFlowTestSuite(),
      joinsTestSuite(),
      caseInsensitiveTestSuite(),
    ],
  });
  suite.execute();
}
