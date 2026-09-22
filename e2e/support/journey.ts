import type { Page } from "@playwright/test";

/** Registers a fresh account through the onboarding wizard and completes
 * activation: registration → optional payment bypass →
 * project "Personal" → dashboard. */
export async function registerAndOnboard(
  page: Page,
  opts: { email: string; password: string; displayName: string },
): Promise<void> {
  await registerAndOpenPaymentStep(page, opts);

  // Payment step: continue without payment so the test stays on the free
  // allowance path.
  await page.getByRole("button", { name: "Continue without payment" }).click();
  await page.waitForURL((url) => url.pathname === "/onboarding");

  // Project step: name the first project and open the dashboard.
  await page.fill("#projectName", "Personal");
  await page.getByRole("button", { name: "Create project and open Dashboard" }).click();
  await page.waitForURL((url) => url.pathname === "/" && !url.search);
}

/** Registers a fresh account and stops on the payment step of the activation
 * wizard. Registration already provisions the immutable "Personal" workspace. */
export async function registerAndOpenPaymentStep(
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
  await page.waitForSelector(".ts-onboarding-plan");
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
