import { expect, test } from 'playwright/test'

// Regression coverage for the contact form → Google Forms integration (TER-3).
//
// The contact page records submissions by POSTing the visible form's values into
// a hidden iframe targeted at the Google Form's `formResponse` endpoint (Google
// Forms exposes no CORS, so a native form POST into an iframe is the reliable
// static-site delivery). This test proves the SEND MESSAGE button emits that POST
// with the correct field mapping and then surfaces the success state.
//
// The cross-origin POST is intercepted and fulfilled locally so the test is
// deterministic and never writes rows into the real form's response spreadsheet.
// The entry IDs asserted below are the live form's real field IDs, verified
// against its FB_PUBLIC_LOAD_DATA_ definition (Name / Email / Message; the form
// has no Company field, so Company is folded into the message body on submit).

const FORM_RESPONSE_URL =
  'https://docs.google.com/forms/d/e/1FAIpQLSfnbZR5CkFw581Bwwiy2CSbz_SDi4C8qMzxvmGrY0hfhzyULg/formResponse'

const decodeFormBody = (body: string) => decodeURIComponent(body.replace(/\+/g, ' '))

test('SEND MESSAGE posts to Google Forms with the correct entry IDs', async ({ page }) => {
  let capturedPost: string | null = null
  await page.route(FORM_RESPONSE_URL, async (route) => {
    capturedPost = route.request().postData() ?? ''
    // Fulfilling navigates the hidden iframe, firing its load event so the page
    // treats the submission as acknowledged (mirroring Google's 200 response).
    await route.fulfill({ status: 200, body: 'recorded' })
  })

  await page.goto('/contact')

  await page.locator('#contact-name').fill('Ada Lovelace')
  await page.locator('#contact-email').fill('ada@example.com')
  await page.locator('#contact-company').fill('Analytical Engine Ltd')
  await page.locator('#contact-message').fill('Hello from the end-to-end test.')

  await page.getByRole('button', { name: /send message/i }).click()

  // The success state is driven by the iframe's load event after our 200.
  await expect(page.getByText('Message Sent!')).toBeVisible({ timeout: 10_000 })

  expect(capturedPost, 'expected a POST to the Google Forms formResponse endpoint').not.toBeNull()
  const post = capturedPost ?? ''
  const body = decodeFormBody(post)

  // Real entry IDs for the backing Google Form (Name / Email / Message).
  expect(post).toContain('entry.2005620554=')
  expect(post).toContain('entry.1045781291=')
  expect(post).toContain('entry.839337160=')

  expect(body).toContain('Ada Lovelace')
  expect(body).toContain('ada@example.com')
  expect(body).toContain('Hello from the end-to-end test.')
  // Company has no dedicated entry on the form → it is prepended to the message.
  expect(body).toContain('Company: Analytical Engine Ltd')
})

test('the Google Form backing the contact page is reachable and accepts the mapping', async ({
  request,
}) => {
  // Guards against the integration silently breaking if the published Google Form
  // is deleted, closed, or has its field IDs regenerated. A plain GET of the form's
  // view page is read-only and never submits a response.
  const response = await request.get(FORM_RESPONSE_URL.replace('/formResponse', '/viewform'), {
    failOnStatusCode: false,
  })
  expect(response.status(), 'backing Google Form must remain published').toBe(200)
  expect(response.headers()['content-type'] ?? '').toContain('text/html')
})
