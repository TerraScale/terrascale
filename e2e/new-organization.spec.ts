import { test, expect } from "@playwright/test";
import { registerAndOnboard } from "./support/journey";

test("member creates another organization through the modal's three onboarding steps", async ({ page }) => {
  // A cold preview plus the Stripe API round-trip needs more than the default.
  test.setTimeout(120_000);
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
  // Picking a plan card updates the posted plan: billing follows the card.
  await dialog.locator(".ts-onboarding-plan-business").click();
  await expect(dialog.locator('input[name="plan"]')).toHaveValue("business");
  await dialog.getByRole("button", { name: "Continue to secure payment setup" }).click();

  // The provider differs per target: the CI webServer runs the local payment
  // provider page this suite can complete, while PR previews run the Stripe
  // sandbox and hand off to Stripe's hosted card form, which an automated test
  // cannot drive. Assert the sandbox hand-off there and stop: the
  // local-provider run covers the rest of the wizard.
  await page.waitForURL(
    url => url.pathname.startsWith("/checkout/") || url.hostname.endsWith("stripe.com") || url.pathname === "/organization",
    { timeout: 60_000 },
  );
  if (new URL(page.url()).hostname.endsWith("stripe.com")) {
    test.skip(true, "Stripe sandbox hands off to Stripe's hosted card form; the local-provider run covers the wizard.");
  }
  expect(new URL(page.url()).pathname, "Stripe sandbox checkout did not start").not.toBe("/organization");

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
