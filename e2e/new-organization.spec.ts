import { test, expect, type Page } from "@playwright/test";
import { registerAndOnboard } from "./support/journey";

/** Completes whichever checkout the target deployment configured: the local
 * demo provider (the Playwright webServer runs with DASHBOARD_E2E=1) or the
 * Stripe sandbox the preview workflow binds to every PR app. */
async function completePaymentSetup(page: Page): Promise<void> {
  await page.waitForURL(
    (url) => url.pathname.startsWith("/checkout/") || url.hostname === "checkout.stripe.com",
    { timeout: 30_000 },
  );
  if (page.url().includes("checkout.stripe.com")) {
    // Stripe's hosted sandbox checkout: pay with its documented test card.
    await page.getByRole("textbox", { name: "Card number" }).fill("4242 4242 4242 4242");
    await page.getByRole("textbox", { name: "Expiration" }).fill("12 / 34");
    await page.getByRole("textbox", { name: "Credit or debit card CVC/CVV" }).fill("123");
    const cardholder = page.getByRole("textbox", { name: "Cardholder name" });
    if (await cardholder.count()) await cardholder.fill("E2E New Org");
    await page.getByRole("button", { name: "Subscribe" }).click();
    return;
  }

  await page.getByRole("button", { name: /Use local test payment method/ }).click();
}

test("member creates another organization through the modal's three onboarding steps", async ({ page }) => {
  // The Stripe-sandbox preview pays through a hosted checkout, so this test
  // needs more than the default budget when it runs against a deployed app.
  test.slow();

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
  await completePaymentSetup(page);
  await page.waitForURL("**/organization?newOrganization=project&payment=success**");

  // Step 3 — the onboarding project screen, naming the new tenant's first project.
  await expect(dialog.getByText("STEP 3 OF 3")).toBeVisible();
  await dialog.locator("#projectName").fill("nova-prod");
  await dialog.getByRole("button", { name: "Create project and open Dashboard" }).click();
  await page.waitForURL((url) => url.pathname === "/organization" && url.search === "");

  await expect(page.locator("#activeOrganization option:checked")).toHaveText("Nova Labs (Business)");
  await expect(page.getByRole("cell", { name: /nova-prod/ }).first()).toBeVisible();
});
