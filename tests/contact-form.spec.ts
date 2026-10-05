import { expect, test } from 'playwright/test'

for (const javaScriptEnabled of [true, false]) {
  test(`contact uses support email with JavaScript ${javaScriptEnabled ? 'enabled' : 'disabled'}`, async ({ browser, baseURL }) => {
    const context = await browser.newContext({ javaScriptEnabled, baseURL })
    const page = await context.newPage()
    const posts: string[] = []
    page.on('request', (request) => {
      if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/v1/public/contact') posts.push(request.url())
    })

    for (const navigate of [() => page.goto('/contact'), () => page.reload()]) {
      await navigate()
      const email = page.locator('.contact-email').getByRole('link', { name: 'support@terrascale.tech' })
      await expect(email).toBeVisible()
      await expect(email).toHaveAttribute('href', 'mailto:support@terrascale.tech')
      await expect(page.locator('form, input, textarea, button[type="submit"]')).toHaveCount(0)
    }

    await page.locator('.contact-email a').focus()
    await expect(page.locator('.contact-email a')).toBeFocused()
    expect(posts).toEqual([])
    await context.close()
  })
}
