import { test, expect } from "@playwright/test";
import { registerAndOnboard } from "./support/journey";

test("member creates another organization through the three onboarding steps", async ({ page }) => {
  await registerAndOnboard(page, {
    email: `e2e-neworg-${Date.now()}@example.test`,
    password: "E2e-neworg-123!",
    displayName: "E2E New Org",
  });

  // The organization page routes through the guided flow instead of an inline form.
  await page.goto("/organization");
  await page.getByRole("link", { name: "Create organization" }).click();
  await expect(page).toHaveURL(/\/organizations\/new/);

  // Step 1 — the onboarding organization screen.
  await expect(page.getByText("STEP 1 OF 3")).toBeVisible();
  await page.fill("#organizationName", "Nova Labs");
  await page.getByRole("button", { name: "Create organization" }).click();
  await page.waitForURL("**/organizations/new?step=payment**");

  // Step 2 — the onboarding payment screen (plan cards), continued without payment.
  await expect(page.getByText("STEP 2 OF 3")).toBeVisible();
  await expect(page.getByText("Register a payment method")).toBeVisible();
  await page.getByRole("button", { name: "Continue without payment" }).click();
  await page.waitForURL("**/organizations/new?step=project**");

  // Step 3 — the onboarding project screen, naming the new tenant's first project.
  await expect(page.getByText("STEP 3 OF 3")).toBeVisible();
  await page.fill("#projectName", "nova-prod");
  await page.getByRole("button", { name: "Create project and open Dashboard" }).click();
  await page.waitForURL("**/organization**");

  await expect(page.locator("#activeOrganization option:checked")).toHaveText("Nova Labs (active)");
  await expect(page.getByRole("cell", { name: /nova-prod/ }).first()).toBeVisible();
});
