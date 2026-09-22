import { test, expect } from "@playwright/test";
import { registerAndOnboard } from "./support/journey";

test("member creates another organization through the modal's three onboarding steps", async ({ page }) => {
  await registerAndOnboard(page, {
    email: `e2e-neworg-${Date.now()}@example.test`,
    password: "E2e-neworg-123!",
    displayName: "E2E New Org",
  });

  // The organization page opens the guided flow in a modal instead of navigating.
  await page.goto("/organization");
  await page.locator('[data-open-dialog="create-organization"]').click();
  const dialog = page.getByRole("dialog", { name: "Create organization" });
  await expect(dialog).toBeVisible();

  // Step 1 — the onboarding organization screen. A new tenant is a paid
  // Business organization, separate from the Personal allowance.
  await expect(dialog.getByText("STEP 1 OF 3")).toBeVisible();
  await dialog.locator("#newOrganizationName").fill("Nova Labs");
  await dialog.getByRole("button", { name: "Create organization" }).click();
  await page.waitForURL("**/organization?newOrganization=payment**");

  // Step 2 — the onboarding payment screen (plan cards) with no bypass.
  await expect(dialog.getByText("STEP 2 OF 3")).toBeVisible();
  await expect(dialog.getByText("Register a payment method")).toBeVisible();
  await dialog.getByRole("button", { name: "Continue to secure payment setup" }).click();
  await page.waitForURL("**/checkout/**");

  // Secure payment setup lands on the local provider's checkout page.
  await page.getByRole("button", { name: /Use local test payment method/ }).click();
  await page.waitForURL("**/organization?newOrganization=project&payment=success**");

  // Step 3 — the onboarding project screen, naming the new tenant's first project.
  await expect(dialog.getByText("STEP 3 OF 3")).toBeVisible();
  await dialog.locator("#projectName").fill("nova-prod");
  await dialog.getByRole("button", { name: "Create project and open Dashboard" }).click();
  await page.waitForURL((url) => url.pathname === "/organization" && url.search === "");

  await expect(page.locator("#activeOrganization option:checked")).toHaveText("Nova Labs (Business)");
  await expect(page.getByRole("cell", { name: /nova-prod/ }).first()).toBeVisible();
});
