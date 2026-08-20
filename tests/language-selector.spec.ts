import { expect, test, type Page } from 'playwright/test'

// Regression coverage for the landing-page language selector (TER-80).
//
// The selector mirrors the documentation-site interaction (pick a language, the
// page content swaps) but is implemented client-side: the landing page
// prerenders a fixed route set, so locale switching is a reactive store change
// persisted to localStorage rather than a path change. These tests prove the
// switch updates the visible copy and the <html lang> attribute, and that the
// choice survives a reload.

// The header dropdown trigger is the only button with this aria attribute (the
// footer/mobile inline variant renders a button group, not a popup trigger), so
// this selector is stable regardless of the active language's label.
const trigger = 'button[aria-haspopup="listbox"]'

async function selectLanguage(page: Page, language: string) {
  const desktopTrigger = page.locator(trigger)

  if (await desktopTrigger.isVisible()) {
    await desktopTrigger.click()
    await page.getByRole('option', { name: language }).click()
    return
  }

  await page.getByRole('button', { name: 'Toggle navigation menu' }).click()
  await page.locator('#mobile-navigation').getByRole('button', { name: language }).click()
}

test('selecting a language swaps the page copy and updates <html lang>', async ({ page }) => {
  await page.goto('/')

  // Prerendered default is English.
  await expect(page.getByText(/A typed document database with asynchronous replication/)).toBeVisible()
  await expect(page.locator('html')).toHaveAttribute('lang', 'en')

  // Switch to Spanish via the header dropdown.
  await selectLanguage(page, 'Español')

  await expect(page.getByText(/Una base de documentos tipada con replicación asíncrona/)).toBeVisible()
  await expect(page.locator('html')).toHaveAttribute('lang', 'es')
})

test('the chosen language persists across reloads', async ({ page }) => {
  await page.goto('/')

  await selectLanguage(page, 'Português (Brasil)')

  await expect(page.getByText(/Um banco de documentos tipado com replicação assíncrona/)).toBeVisible()
  await expect(page.locator('html')).toHaveAttribute('lang', 'pt-BR')

  // The inline pre-paint script + onMount init read the stored choice, so the
  // Portuguese copy reappears after a full reload with no further interaction.
  await page.reload()
  await expect(page.getByText(/Um banco de documentos tipado com replicação assíncrona/)).toBeVisible()
  await expect(page.locator('html')).toHaveAttribute('lang', 'pt-BR')
})

test('the footer inline selector also switches the language', async ({ page }) => {
  await page.goto('/')

  // The footer renders a flat button row (variant="inline"); use it to switch
  // to Spanish and confirm the header trigger reflects the new short code.
  await page.getByRole('group', { name: /idioma|language|selecciona/i }).getByRole('button', { name: 'Español' }).click()

  await expect(page.getByText(/Una base de documentos tipada con replicación asíncrona/)).toBeVisible()
  await expect(page.locator(trigger)).toContainText('ES')
})
