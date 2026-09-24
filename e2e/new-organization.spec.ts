import { test, expect } from "./support/test";
import { registerAndOnboard } from "./support/journey";

test("demo billing upgrades a plan through the local simulator without contacting Stripe", async ({ page }) => {
  await registerAndOnboard(page, {
    email: `e2e-billing-${Date.now()}@example.test`,
    password: "E2e-billing-123!",
    displayName: "E2E Billing",
  });

  await page.goto("/subscriptions");
  await expect(page.getByRole("heading", { name: "Plans and billing" })).toBeVisible();
  await expect(page.getByText("Demo billing")).toBeVisible();

  const proPlan = page.locator("article").filter({ has: page.getByRole("heading", { name: "Pro", exact: true }) });
  await proPlan.getByRole("button", { name: "Choose monthly" }).click();
  await page.waitForURL(/\/checkout\/cs_local_/);

  await expect(page.getByRole("heading", { name: "Local payment setup" })).toBeVisible();
  await expect(page.getByRole("status")).toContainText("Demo simulator only");
  await expect(page.getByRole("status")).toContainText("No card details are collected");
  await expect(page.getByRole("button", { name: "Confirm local demo setup" })).toBeVisible();

  await page.getByRole("button", { name: "Confirm local demo setup" }).click();
  await page.waitForURL("**/subscriptions?payment=success");
  const currentPlan = page.locator('section[aria-labelledby="billing-summary-title"]');
  await expect(currentPlan).toContainText("Pro");
  await expect(currentPlan).toContainText("Active");
  await expect(currentPlan).toContainText("On file");

  await page.getByRole("link", { name: "Manage billing" }).click();
  await expect(page.getByRole("heading", { name: "Local billing portal" })).toBeVisible();
  await expect(page.getByRole("status")).toContainText("Demo simulator only");
  await expect(page.getByRole("status")).toContainText("does not collect payment details or contact Stripe");
});
