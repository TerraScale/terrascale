import { expect, test, type Page } from 'playwright/test'

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

  await expect(page.getByText(/A typed document database with asynchronous replication/)).toBeVisible()
  await expect(page.locator('html')).toHaveAttribute('lang', 'en')

  await selectLanguage(page, 'Español')

  await expect(page.getByText(/Una base de documentos tipada con replicación asíncrona/)).toBeVisible()
  await expect(page.locator('html')).toHaveAttribute('lang', 'es')
})

test('the chosen language persists across reloads', async ({ page }) => {
  await page.goto('/')

  await selectLanguage(page, 'Português (Brasil)')

  await expect(page.getByText(/Um banco de documentos tipado com replicação assíncrona/)).toBeVisible()
  await expect(page.locator('html')).toHaveAttribute('lang', 'pt-BR')

  await page.reload()
  await expect(page.getByText(/Um banco de documentos tipado com replicação assíncrona/)).toBeVisible()
  await expect(page.locator('html')).toHaveAttribute('lang', 'pt-BR')
})

test('the footer inline selector also switches the language', async ({ page }) => {
  await page.goto('/')

  await page.getByRole('group', { name: /idioma|language|selecciona/i }).getByRole('button', { name: 'Español' }).click()

  await expect(page.getByText(/Una base de documentos tipada con replicación asíncrona/)).toBeVisible()
  await expect(page.locator(trigger)).toContainText('ES')
})
