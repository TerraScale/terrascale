import { expect, test } from 'playwright/test'

const CONTACT_API = '**/api/v1/public/contact'

test('clears the contact form only after durable API acceptance', async ({ page }) => {
  let submitted: Record<string, string> | null = null
  await page.route(CONTACT_API, async (route) => {
    submitted = route.request().postDataJSON() as Record<string, string>
    await route.fulfill({
      status: 202,
      contentType: 'application/json',
      body: JSON.stringify({ submissionId: '01KTEST0000000000000000000', accepted: true }),
    })
  })

  await page.goto('/contact')
  await page.locator('#contact-name').fill('Ada Lovelace')
  await page.locator('#contact-email').fill('ada@example.com')
  await page.locator('#contact-company').fill('Analytical Engine Ltd')
  await page.locator('#contact-message').fill('Hello from the end-to-end test.')
  await page.getByRole('button', { name: /send message/i }).click()

  await expect(page.getByText('Message Sent!')).toBeVisible()
  await expect(page.locator('#contact-name')).toHaveValue('')
  await expect(page.locator('#contact-message')).toHaveValue('')
  expect(submitted).toEqual({
    name: 'Ada Lovelace',
    email: 'ada@example.com',
    subject: 'Contact from Analytical Engine Ltd',
    message: 'Hello from the end-to-end test.',
  })
})

test('preserves contact form values when durable acceptance fails', async ({ page }) => {
  await page.route(CONTACT_API, (route) =>
    route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ accepted: false }) }),
  )
  await page.goto('/contact')
  await page.locator('#contact-name').fill('Grace Hopper')
  await page.locator('#contact-email').fill('grace@example.com')
  await page.locator('#contact-message').fill('Please contact me about TerraScale.')
  await page.getByRole('button', { name: /send message/i }).click()

  await expect(page.locator('.contact-form__status.is-error')).toBeVisible()
  await expect(page.locator('#contact-name')).toHaveValue('Grace Hopper')
  await expect(page.locator('#contact-message')).toHaveValue('Please contact me about TerraScale.')
})
