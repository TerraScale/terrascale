import { expect, test } from 'playwright/test'

// Header layout regression for TER-86 (language selector placed after the
// "Começar" / Get Started CTA) and the no-overlap invariant it depends on.
//
// The landing-page header packs a wide action group (Docs, Status, the primary
// CTA, and the language selector) to the right of a six-link primary nav. With
// that much content the nav cannot sit on the page's geometric centre without
// overlapping the actions, so the header centres the nav in the *available*
// space (flex + `margin: 0 auto`) instead. These tests pin the two invariants
// that actually matter for a balanced, non-overlapping header:
//   1. the language selector renders directly after the CTA (last action), and
//   2. the centred nav never overlaps the action group at desktop widths.
// Below the desktop breakpoint the whole bar collapses into the mobile menu.

test.describe('header layout (TER-86)', () => {
  test('the language selector renders directly after the Começar CTA', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 720 })
    await page.goto('/')
    await page.waitForLoadState('networkidle')

    // Direct children of the action group, tagged by what they are. Order is the
    // whole point of TER-86: the selector must be last, immediately after the CTA
    // (it sat before Docs in TER-80).
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
        // Docs is the leftmost action, so its left edge is the nearest one to
        // the nav — the tightest gap in the header.
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
    const iconPath = toggle.locator('path')
    const closedPath = await iconPath.getAttribute('d')

    await toggle.click()
    await expect(page.locator('#mobile-navigation')).toBeVisible()
    await expect.poll(() => iconPath.getAttribute('d')).not.toBe(closedPath)

    await toggle.click()
    await expect(page.locator('#mobile-navigation')).toBeHidden()
    await expect.poll(() => iconPath.getAttribute('d')).toBe(closedPath)
  })

  test('a shared icon retries after a transient load failure', async ({ page }) => {
    let attempts = 0
    await page.route('**/icons/lucide/home.svg', async (route) => {
      attempts++
      if (attempts === 1) await route.abort('failed')
      else await route.continue()
    })
    await page.setViewportSize({ width: 1100, height: 720 })
    await page.goto('/')
    await expect.poll(() => attempts).toBe(1)

    await page.getByRole('button', { name: /toggle navigation menu/i }).click()

    await expect.poll(() => attempts).toBe(2)
    await expect(page.locator('#mobile-navigation a[href="/"] .ts-icon svg')).toHaveCount(1)
  })
})
