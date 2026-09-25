import { expect, test } from "vitest";
import {
  authFlowTestSuite,
  caseInsensitiveTestSuite,
  joinsTestSuite,
  normalTestSuite,
  testAdapter,
  transactionsTestSuite,
  uuidTestSuite,
} from "@better-auth/test-utils/adapter";
import {
  AuthPersistenceClient,
  AuthTransactionOutcomeUnknownError,
  createTerraScaleAuthAdapter,
  runAuthTransaction,
} from "@terrascale/sdk/better-auth";

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

  test("reconciles a committed organization mutation receipt after a lost commit and fresh client", async () => {
    const actorId = `authority-receipt-actor-${globalThis.crypto.randomUUID()}`;
    const organizationId = `authority-receipt-organization-${globalThis.crypto.randomUUID()}`;
    const operationKey = `authority-receipt-operation-${globalThis.crypto.randomUUID()}`;
    const action = "request-deletion";
    const organizationName = "External authority receipt qualification";
    const fingerprint = JSON.stringify({ organizationId, organizationName });
    const originalTimestamp = "2026-09-08T00:00:00.000Z";
    const receiptId = `authority-receipt-${await sha256(`${actorId}\n${organizationId}\n${action}\n${operationKey}`)}`;
    const transactionLabel = `${operationKey}:transaction`;
    const beginKey = `${operationKey}:begin`;
    const commitKey = `${operationKey}:commit`;
    const rollbackKey = `${operationKey}:rollback`;
    let lostCommit = true;
    let commitRequests = 0;

    const fixtureFetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      const request = new Request(input, init);
      const response = await fetch(request);
      if (new URL(request.url).pathname.endsWith("/transactions/commit")) {
        commitRequests += 1;
        if (lostCommit) {
          lostCommit = false;
          throw new TypeError("qualification deliberately dropped the commit response");
        }
      }
      return response;
    };

    const client = new AuthPersistenceClient({ apiBaseUrl, accessToken, fetch: fixtureFetch });
    let unknown: AuthTransactionOutcomeUnknownError | undefined;
    try {
      await runAuthTransaction(
        client,
        database,
        async (operations) => {
          const organization = await operations.insertUnique(
            {
              model: "organization",
              record: {
                id: organizationId,
                name: organizationName,
                metadata: {
                  status: "DeletionPending",
                  deletionRequestedAt: originalTimestamp,
                  deletionRequestedByUserId: actorId,
                },
              },
            },
            { idempotencyKey: `${operationKey}:organization` },
          );
          expect(organization.ok).toBe(true);

          const receipt = await operations.insertUnique(
            {
              model: "organizationMutation",
              record: {
                id: receiptId,
                actorId,
                organizationId,
                action,
                fingerprint,
                result: {
                  id: organizationId,
                  name: organizationName,
                  metadata: {
                    status: "DeletionPending",
                    deletionRequestedAt: originalTimestamp,
                    deletionRequestedByUserId: actorId,
                  },
                },
                createdAt: originalTimestamp,
              },
            },
            { idempotencyKey: `${operationKey}:receipt` },
          );
          expect(receipt.ok).toBe(true);
        },
        {
          transactionIdFactory: () => transactionLabel,
          beginIdempotencyKeyFactory: () => beginKey,
          commitIdempotencyKeyFactory: () => commitKey,
          rollbackIdempotencyKeyFactory: () => rollbackKey,
        },
      );
      throw new Error("The authority unexpectedly returned the dropped commit response.");
    } catch (error) {
      expect(error).toBeInstanceOf(AuthTransactionOutcomeUnknownError);
      if (error instanceof AuthTransactionOutcomeUnknownError) {
        unknown = error;
        expect(error.commitIdempotencyKey).toBe(commitKey);
        expect(error.transactionId.startsWith(`${transactionLabel}#`)).toBe(true);
      }
    }

    expect(unknown).toBeDefined();
    expect(commitRequests).toBe(1);

    // A new client instance must resolve the same committed transaction identity;
    // replaying the callback would create a second logical organization mutation.
    const restartedClient = new AuthPersistenceClient({ apiBaseUrl, accessToken, fetch: fixtureFetch });
    const resolved = await restartedClient.commitTransaction(
      database,
      { transactionId: unknown!.transactionId },
      { idempotencyKey: unknown!.commitIdempotencyKey },
    );
    expect(resolved.ok).toBe(true);
    expect(commitRequests).toBe(2);

    const storedReceipt = await restartedClient.findOne(database, "organizationMutation", receiptId);
    expect(storedReceipt.ok).toBe(true);
    expect(storedReceipt.ok && storedReceipt.value.record).toMatchObject({
      id: receiptId,
      actorId,
      organizationId,
      action,
      fingerprint,
      createdAt: originalTimestamp,
    });
    const storedResult = storedReceipt.ok ? storedReceipt.value.record?.result : undefined;
    expect(storedResult).toMatchObject({
      id: organizationId,
      metadata: { deletionRequestedAt: originalTimestamp, deletionRequestedByUserId: actorId },
    });

    const wrongActorReceipt = await restartedClient.findOne(
      database,
      "organizationMutation",
      `authority-receipt-${await sha256(`other-actor\n${organizationId}\n${action}\n${operationKey}`)}`,
    );
    expect(wrongActorReceipt.ok).toBe(true);
    expect(wrongActorReceipt.ok && wrongActorReceipt.value.record).toBeNull();
    const wrongKeyReceipt = await restartedClient.findOne(
      database,
      "organizationMutation",
      `authority-receipt-${await sha256(`${actorId}\n${organizationId}\n${action}\nother-operation-key`)}`,
    );
    expect(wrongKeyReceipt.ok).toBe(true);
    expect(wrongKeyReceipt.ok && wrongKeyReceipt.value.record).toBeNull();

    const replayedOrganization = await restartedClient.findOne(database, "organization", organizationId);
    expect(replayedOrganization.ok).toBe(true);
    expect(replayedOrganization.ok && replayedOrganization.value.record).toMatchObject({
      id: organizationId,
      metadata: { deletionRequestedAt: originalTimestamp, deletionRequestedByUserId: actorId },
    });
  }, 30_000);
}

async function sha256(value: string): Promise<string> {
  const digest = await globalThis.crypto.subtle.digest("SHA-256", new TextEncoder().encode(value));
  return Array.from(new Uint8Array(digest), (byte) => byte.toString(16).padStart(2, "0")).join("");
}
