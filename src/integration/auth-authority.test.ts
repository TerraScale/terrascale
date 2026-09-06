import { test } from "vitest";
import {
  authFlowTestSuite,
  caseInsensitiveTestSuite,
  joinsTestSuite,
  normalTestSuite,
  testAdapter,
  transactionsTestSuite,
  uuidTestSuite,
} from "@better-auth/test-utils/adapter";
import { AuthPersistenceClient, createTerraScaleAuthAdapter } from "@terrascale/sdk/better-auth";

const apiBaseUrl = process.env.TERRASCALE_AUTH_TEST_BASE_URL;

if (apiBaseUrl === undefined) {
  test.skip("requires the disposable encrypted auth authority fixture", () => undefined);
} else {
  // The official suites delete every row in their models during cleanup.
  // This entrypoint only accepts the disposable loopback producer fixture.
  const url = new URL(apiBaseUrl);
  if (url.protocol !== "http:" || url.hostname !== "127.0.0.1") {
    throw new Error("Auth adapter qualification requires a disposable loopback authority.");
  }
  const database = process.env.TERRASCALE_AUTH_TEST_DATABASE;
  const accessToken = process.env.TERRASCALE_AUTH_TEST_ACCESS_TOKEN;
  if (!database || !accessToken) throw new Error("Auth authority fixture credentials are required.");
  const client = new AuthPersistenceClient({ apiBaseUrl, accessToken });
  const factory = createTerraScaleAuthAdapter(client, database);

  const suite = await testAdapter({
    adapter: () => factory,
    runMigrations: async (options) => {
      // Records are schemaless. The official factory installs every declared
      // unique constraint before this quorum-confirmed read can complete.
      await factory(options).count({ model: "user" });
    },
    tests: [
      normalTestSuite(),
      transactionsTestSuite(),
      authFlowTestSuite(),
      joinsTestSuite(),
      caseInsensitiveTestSuite(),
      uuidTestSuite(),
    ],
  });
  suite.execute();
}
