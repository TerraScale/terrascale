import type { Locator, Page } from "@playwright/test";

export type AccountCredentials = {
  email: string;
  password: string;
  displayName: string;
};

/** Creates a fresh demo account and finishes the payment and project steps. */
export async function registerAndOnboard(
  page: Page,
  opts: AccountCredentials,
): Promise<void> {
  await registerAndOpenPaymentStep(page, opts);

  const payment = await onboardingStep(page, "payment-method");
  await payment.getByRole("button", { name: "Continue without adding a payment method" }).click();

  const project = await onboardingStep(page, "project");
  await project.locator('[name="name"]').fill("Personal");
  await project.locator('[name="region"]').selectOption("GRU");
  await project.getByRole("button", { name: "Create project" }).click();
  await page.waitForURL((url) => url.pathname === "/" && !url.search);
}

/** Registers an account and leaves it on the payment-method step. */
export async function registerAndOpenPaymentStep(
  page: Page,
  opts: AccountCredentials,
): Promise<void> {
  await page.goto("/onboarding");
  const form = page.locator('form[action="/onboarding/register"]');
  await form.locator('[name="displayName"]').fill(opts.displayName);
  await form.locator('[name="email"]').fill(opts.email);
  await form.locator('[name="password"]').fill(opts.password);
  await form.locator('[name="confirmPassword"]').fill(opts.password);
  await form.locator('[name="terms"]').check();
  await form.getByRole("button", { name: "Save" }).click();

  await page.waitForURL((url) => url.pathname === "/onboarding");
  await onboardingStep(page, "payment-method");
}

export async function login(page: Page, email: string, password: string): Promise<void> {
  await page.goto("/login");
  const form = page.locator('form[action="/api/v1/auth/password/login"]');
  await form.locator('[name="email"]').fill(email);
  await form.locator('[name="password"]').fill(password);
  await form.getByRole("button", { name: "Sign in" }).click();
  await page.waitForURL((url) => url.pathname !== "/login");
}

export async function logout(page: Page): Promise<void> {
  await page.goto("/");
  await page.locator('form[action="/api/v1/auth/logout"]').evaluate((form) => {
    (form as HTMLFormElement).requestSubmit();
  });
  await page.waitForURL("**/login**");
}

async function onboardingStep(page: Page, name: string): Promise<Locator> {
  const form = page.locator('form[action="/onboarding/step"]').filter({
    has: page.locator(`[name="step"][value="${name}"]`),
  });
  await form.waitFor({ state: "visible" });
  return form;
}
