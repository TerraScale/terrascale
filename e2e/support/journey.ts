import type { Page } from "@playwright/test";

/** Registers a fresh account through the v1 onboarding wizard and returns its
 * credentials. Walks activation: registration → optional payment bypass →
 * project "Personal" → dashboard. */
export async function registerAndOnboard(
  page: Page,
  opts: { email: string; password: string; displayName: string },
): Promise<void> {
  await page.goto("/onboarding");
  await page.fill('input[name="displayName"]', opts.displayName);
  await page.fill('input[name="email"]', opts.email);
  await page.fill('input[name="password"]', opts.password);
  await page.fill('input[name="confirmPassword"]', opts.password);
  await page.check('input[name="terms"]');
  await page.click('button[type="submit"]');
  await page.waitForURL("**/onboarding**");

  // Organization step: create "Personal" (submits straight to payment).
  await page.fill("#organizationName", "Personal");
  await page.getByRole("button", { name: "Create organization" }).click();
  await page.waitForURL("**/onboarding?step=payment-method**");

  // Payment step: local checkout (validation completes), landing on project.
  await page.getByRole("button", { name: "Continue to secure payment setup" }).click();
  await page.waitForURL("**/checkout/**");
  await page.click('form[action*="/checkout/"] button[type="submit"]');
  await page.waitForURL("**payment=success**");

  // Project step: name it "Personal" and open the dashboard.
  await page.fill("#projectName", "Personal");
  await page.getByRole("button", { name: "Create project and open Dashboard" }).click();
  await page.waitForURL((url) => url.pathname === "/" && !url.search);
}

export async function login(page: Page, email: string, password: string): Promise<void> {
  await page.goto("/login");
  await page.fill("#email", email);
  await page.fill("#password", password);
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.waitForURL((url) => !url.pathname.startsWith("/login"));
}

export async function logout(page: Page): Promise<void> {
  await page.request.post("/api/v1/auth/logout");
  await page.context().clearCookies();
}
