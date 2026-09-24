import { randomUUID } from "node:crypto";
import { existsSync } from "node:fs";
import { pathToFileURL } from "node:url";
import { expect, test, type Page } from "@playwright/test";
import { registerAndOnboard } from "../../e2e/support/journey";
import { observeBrowserErrors } from "../../e2e/support/test";
import { startSDKFixture } from "./support/sdk-fixture-process";

const sdkEntry = process.env.TERRASCALE_TYPESCRIPT_SDK_ENTRY
  ? pathToFileURL(process.env.TERRASCALE_TYPESCRIPT_SDK_ENTRY)
  : new URL("./sdk/dist/index.js", import.meta.url);

type SDKClient = {
  documents: {
    put(database: string, collection: string, id: string, value: unknown, options?: { idempotencyKey?: string }): Promise<SDKResult<{ revision: string }>>;
    get<T>(database: string, collection: string, id: string): Promise<SDKResult<T>>;
    delete(database: string, collection: string, id: string, options?: { idempotencyKey?: string }): Promise<SDKResult<void>>;
  };
};

type SDKResult<T> =
  | { ok: true; status: number; value: T }
  | { ok: false; status?: number; error: { status?: number; message: string } };

type ProjectAPIKeySecret = {
  apiKey: { databaseId: string; scopes: string[] };
  secret: string;
};

type Database = { id: string; name: string; status: string };

test("account-created database works through issued credentials and the TypeScript SDK", async ({ browser }) => {
  expect(existsSync(sdkEntry), `build the pinned TypeScript SDK at ${sdkEntry.href}`).toBe(true);
  const { createTerraScaleClient } = await import(sdkEntry.href) as {
    createTerraScaleClient(config: {
      apiBaseUrl: string;
      accessToken: string;
      tenantId: string;
      projectId: string;
      routing: { enabled: false };
    }): SDKClient;
  };

  const fixture = await startSDKFixture();
  const context = await browser.newContext({ baseURL: fixture.endpoints.dashboardUrl });
  try {
    const page = await context.newPage();
    const browserErrors = observeBrowserErrors(page);
    const email = `sdk-e2e-${randomUUID()}@example.test`;
    await registerAndOnboard(page, {
      email,
      password: "E2e-sdk-journey-123!",
      displayName: "SDK Integration",
    });

    await page.goto("/databases");
    const projectId = await page.locator("main[data-project-id]").getAttribute("data-project-id");
    expect(projectId, "onboarding must provision a selected project").toBeTruthy();
    const tenantId = await page.locator("[data-active-organization-id]").getAttribute("data-active-organization-id");
    expect(tenantId, "the dashboard shell must expose the active organization scope").toBeTruthy();
    const primary = await createDatabase(page, `sdk-e2e-${randomUUID()}`);
    const other = await createDatabase(page, `sdk-e2e-${randomUUID()}`);

    const session = (await context.cookies()).find((cookie) => cookie.name === "ts_dashboard_session");
    expect(session, "account creation must establish a dashboard session").toBeDefined();
    const issueKey = async (name: string, databaseId: string, scopes: string[]) => {
      const response = await page.request.post(`/api/v1/projects/${projectId}/auth/api-keys`, {
        headers: { Authorization: `Bearer ${session!.value}` },
        data: { name, databaseId, scopes },
      });
      const responseBody = await response.text();
      expect(response.status(), responseBody).toBe(201);
      return JSON.parse(responseBody) as ProjectAPIKeySecret;
    };

    const writeKey = await issueKey("SDK fixture writer", primary.id, ["database:read", "database:write"]);
    const readOnlyKey = await issueKey("SDK fixture reader", primary.id, ["database:read"]);
    expect(writeKey.apiKey).toMatchObject({ databaseId: primary.id, scopes: ["database:read", "database:write"] });
    expect(readOnlyKey.apiKey).toMatchObject({ databaseId: primary.id, scopes: ["database:read"] });
    expect(writeKey.secret).not.toMatch(/^tsk_demo_/);
    expect(readOnlyKey.secret).not.toMatch(/^tsk_demo_/);

    const client = createTerraScaleClient({
      apiBaseUrl: fixture.endpoints.publicApiUrl,
      accessToken: writeKey.secret,
      tenantId: tenantId!,
      projectId: projectId!,
      routing: { enabled: false },
    });
    const readOnlyClient = createTerraScaleClient({
      apiBaseUrl: fixture.endpoints.publicApiUrl,
      accessToken: readOnlyKey.secret,
      tenantId: tenantId!,
      projectId: projectId!,
      routing: { enabled: false },
    });
    const invalidClient = createTerraScaleClient({
      apiBaseUrl: fixture.endpoints.publicApiUrl,
      accessToken: "invalid-sdk-fixture-key",
      tenantId: tenantId!,
      projectId: projectId!,
      routing: { enabled: false },
    });

    const documentId = `account-${randomUUID()}`;
    const expected = { account: email, source: "ts-typescript", documentId };
    const created = await client.documents.put(primary.name, "accounts", documentId, expected, {
      idempotencyKey: randomUUID(),
    });
    expect(created.ok, created.ok ? undefined : created.error.message).toBe(true);
    if (!created.ok) return;
    expect(created.value.revision).toBeTruthy();

    const read = await client.documents.get<typeof expected>(primary.name, "accounts", documentId);
    expect(read.ok, read.ok ? undefined : read.error.message).toBe(true);
    if (!read.ok) return;
    expect(read.value).toEqual(expected);

    const invalid = await invalidClient.documents.get(primary.name, "accounts", documentId);
    expectFailureStatus(invalid, 401);

    const scopedRead = await readOnlyClient.documents.get(primary.name, "accounts", documentId);
    expect(scopedRead.ok, scopedRead.ok ? undefined : scopedRead.error.message).toBe(true);
    if (scopedRead.ok) expect(scopedRead.value).toEqual(expected);
    const scopedWrite = await readOnlyClient.documents.put(primary.name, "accounts", `${documentId}-blocked`, { blocked: true }, {
      idempotencyKey: randomUUID(),
    });
    expectFailureStatus(scopedWrite, 403);

    const crossDatabaseRead = await client.documents.get(other.name, "accounts", documentId);
    expectFailureStatus(crossDatabaseRead, 403);

    const deleted = await client.documents.delete(primary.name, "accounts", documentId, { idempotencyKey: randomUUID() });
    expect(deleted.ok, deleted.ok ? undefined : deleted.error.message).toBe(true);
    const afterDelete = await client.documents.get(primary.name, "accounts", documentId);
    expectFailureStatus(afterDelete, 404);
    expect(browserErrors, "SDK account-creation browser console and uncaught errors").toEqual([]);
  } finally {
    try {
      await context.close().catch(() => undefined);
    } finally {
      await fixture.stop();
    }
  }
});

async function createDatabase(page: Page, name: string): Promise<Database> {
  await page.goto("/databases");
  const form = page.locator('form[action="/api/v1/dashboard/databases"]');
  await form.locator('[name="name"]').fill(name);
  await form.locator('[name="region"]').selectOption("GRU");
  await form.getByRole("button", { name: "Create database" }).click();
  await page.waitForURL((url) => url.pathname.startsWith("/databases/db_"));

  const response = await page.request.get("/api/v1/dashboard/databases");
  expect(response.status()).toBe(200);
  const databases = await response.json() as Database[];
  const database = databases.find((candidate) => candidate.name === name);
  expect(database, "the newly created database must be listed in its project").toBeDefined();
  expect(database!.status).toBe("ready");
  return database!;
}

function expectFailureStatus(result: SDKResult<unknown>, status: number): void {
  expect(result.ok, result.ok ? `expected HTTP ${status}, got success` : result.error.message).toBe(false);
  if (!result.ok) expect(result.status ?? result.error.status).toBe(status);
}
