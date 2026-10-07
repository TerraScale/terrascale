import { expect, test } from 'playwright/test'

test.describe('header layout (TER-86)', () => {
  test('the language selector renders directly after the Começar CTA', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 720 })
    await page.goto('/')
    await page.waitForLoadState('networkidle')

    const order = await page.evaluate(() => {
      const actions = document.querySelector('.ts-header__actions')!
      return Array.from(actions.children).map((el) => ({
        isCta: el.classList.contains('ts-cta'),
        isLang: !!el.querySelector('button[aria-haspopup="listbox"]'),
      }))
    })

    expect(order[order.length - 1].isLang, 'selector is the last action').toBe(true)
    expect(order[order.length - 2].isCta, 'CTA is immediately before the selector').toBe(true)
  })

  for (const width of [1440, 1366, 1280]) {
    test(`the centred nav does not overlap the action group at ${width}px`, async ({ page }) => {
      await page.setViewportSize({ width, height: 720 })
      await page.goto('/')
      await page.waitForLoadState('networkidle')

      const boxes = await page.evaluate(() => {
        const nav = document.querySelector('.ts-nav')!.getBoundingClientRect()
        const docs = document.querySelector('.ts-header__docs')!.getBoundingClientRect()
        return { navRight: nav.right, docsLeft: docs.left }
      })

      expect(boxes.docsLeft - boxes.navRight, 'nav must not overlap the Docs link').toBeGreaterThanOrEqual(0)
    })
  }

  test('the header collapses into the mobile menu below the desktop breakpoint', async ({ page }) => {
    await page.setViewportSize({ width: 1100, height: 720 })
    await page.goto('/')
    await page.waitForLoadState('networkidle')

    await expect(page.locator('.ts-nav')).toBeHidden()
    await expect(page.locator('.ts-header__actions')).toBeHidden()
    const toggle = page.getByRole('button', { name: /toggle navigation menu/i })
    await expect(toggle).toBeVisible()
    const icon = toggle.locator('.ts-icon')
    const closedMask = await icon.evaluate((element) => getComputedStyle(element).maskImage)

    await toggle.click()
    await expect(page.locator('#mobile-navigation')).toBeVisible()
    await expect.poll(() => icon.evaluate((element) => getComputedStyle(element).maskImage)).not.toBe(closedMask)

    await toggle.click()
    await expect(page.locator('#mobile-navigation')).toBeHidden()
    await expect.poll(() => icon.evaluate((element) => getComputedStyle(element).maskImage)).toBe(closedMask)
  })

  test('shared icons render checked-in assets without injected markup', async ({ page }) => {
    await page.setViewportSize({ width: 1100, height: 720 })
    await page.goto('/')
    await page.getByRole('button', { name: /toggle navigation menu/i }).click()

    const homeLink = page.getByRole('navigation', { name: 'Mobile primary' }).getByRole('link', { name: 'Home', exact: true })
    await expect.poll(() => homeLink.evaluate((element) => new URL((element as HTMLAnchorElement).href).pathname)).toBe('/')
    const homeIcon = homeLink.locator('.ts-icon')
    await expect(homeIcon).toBeVisible()
    await expect.poll(() => homeIcon.evaluate((element) => getComputedStyle(element).maskImage)).toContain('/icons/lucide/home.svg')
    await expect(homeIcon.locator('svg')).toHaveCount(0)
  })
})
