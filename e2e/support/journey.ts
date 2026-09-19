import type { Page } from "@playwright/test";

/** Registers a fresh account through the onboarding wizard and returns its
 * credentials. Mirrors the registerAccount flow of the visual review capture:
 * onboarding form → organization "Personal" → local checkout → project
 * "Personal" → dashboard. */
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

  // Wizard: organization → local payment → project → dashboard.
  await page.fill('input[name="name"]', "Personal");
  await page.click('button[type="submit"]');
  await page.click('button:has-text("Continue to secure payment setup")');
  await page.waitForURL("**/checkout/**");
  await page.click('form[action*="/checkout/"] button[type="submit"]');
  await page.waitForURL("**payment=success**");
  await page.fill('input[name="name"]', "Personal");
  await page.click('button[type="submit"]');
  await page.waitForURL(page.url().split("?")[0] + "**");
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
