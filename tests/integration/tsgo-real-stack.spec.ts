import { spawn } from "node:child_process";
import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import { registerAndOnboard } from "../../e2e/support/journey";
import { observeBrowserErrors } from "../../e2e/support/test";
import { startSDKFixture } from "./support/sdk-fixture-process";

type ProjectAPIKeySecret = {
  apiKey: { databaseId: string; scopes: string[] };
  secret: string;
};

type Database = { id: string; name: string; status: string };

type GoClientInput = {
  apiBaseUrl: string;
  account: string;
  tenantId: string;
  projectId: string;
  primaryDatabase: string;
  otherDatabase: string;
  documentId: string;
  writeKey: string;
  readOnlyKey: string;
};

test("account-created database works through issued credentials and ts-go", async ({ browser }) => {
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

    const writeKey = await issueKey("Go SDK fixture writer", primary.id, ["database:read", "database:write"]);
    const readOnlyKey = await issueKey("Go SDK fixture reader", primary.id, ["database:read"]);
    expect(writeKey.apiKey).toMatchObject({ databaseId: primary.id, scopes: ["database:read", "database:write"] });
    expect(readOnlyKey.apiKey).toMatchObject({ databaseId: primary.id, scopes: ["database:read"] });
    expect(writeKey.secret).not.toMatch(/^tsk_demo_/);
    expect(readOnlyKey.secret).not.toMatch(/^tsk_demo_/);

    await runGoClient({
      apiBaseUrl: fixture.endpoints.publicApiUrl,
      account: email,
      tenantId: tenantId!,
      projectId: projectId!,
      primaryDatabase: primary.name,
      otherDatabase: other.name,
      documentId: `account-${randomUUID()}`,
      writeKey: writeKey.secret,
      readOnlyKey: readOnlyKey.secret,
    });
    expect(browserErrors, "Go SDK account-creation browser console and uncaught errors").toEqual([]);
  } finally {
    try {
      await context.close().catch(() => undefined);
    } finally {
      await fixture.stop();
    }
  }
});

async function runGoClient(input: GoClientInput): Promise<void> {
  await new Promise<void>((resolve, reject) => {
    const child = spawn("go", ["run", "-mod=vendor", "./tests/integration/tsgo-client"], {
      cwd: process.cwd(),
      stdio: ["pipe", "pipe", "pipe"],
    });
    let output = "";
    child.stdout.on("data", (chunk: Buffer) => { output += chunk.toString(); });
    child.stderr.on("data", (chunk: Buffer) => { output += chunk.toString(); });
    child.on("error", reject);
    child.on("exit", (code) => {
      if (code === 0) resolve();
      else reject(new Error(`ts-go client exited with ${code}: ${output}`));
    });
    child.stdin.end(JSON.stringify(input));
  });
}

async function createDatabase(page: Page, name: string): Promise<Database> {
  await page.goto("/databases");
  const databaseDisclosure = page.locator("#create-database");
  await databaseDisclosure.locator(":scope > summary").click();
  await expect(databaseDisclosure).toHaveAttribute("open", "");
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
