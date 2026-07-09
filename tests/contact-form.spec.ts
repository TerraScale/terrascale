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
//
// TER-85: the second test is a read-only guard against the silent-drop failure
// modes — a deleted, closed, or entry-ID-regenerated form would record nothing
// while the page still reports success (Google's cross-origin response is opaque,
// so the contact page genuinely cannot tell). It reads the published form
// definition and asserts the hard-coded entry IDs still match the live fields.

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

  // TER-81: a confirmation badge must appear in the bottom-right of the contact box.
  const badge = page.locator('.contact-form__badge')
  await expect(badge).toBeVisible()
  await expect(badge).toHaveText(/message sent successfully/i)

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

test('the backing Google Form is live and its entry IDs still match the code (TER-85)', async ({
  request,
}) => {
  // Read-only guard against the silent-drop failure modes behind TER-85: a
  // deleted, closed, or entry-ID-regenerated form would record nothing while the
  // contact page still reports success (Google's cross-origin response is opaque,
  // so the page cannot tell). A GET of the form's view page never submits — it
  // only reads the published form definition.
  const response = await request.get(FORM_RESPONSE_URL.replace('/formResponse', '/viewform'), {
    failOnStatusCode: false,
  })
  expect(response.status(), 'backing Google Form must remain published').toBe(200)
  expect(response.headers()['content-type'] ?? '').toContain('text/html')

  const body = await response.text()
  // The published form definition is embedded as FB_PUBLIC_LOAD_DATA_ = [...];
  const match = body.match(/FB_PUBLIC_LOAD_DATA_\s*=\s*(\[[\s\S]*?\]);/)
  expect(match, 'FB_PUBLIC_LOAD_DATA_ definition must be present on the form page').not.toBeNull()

  const data = JSON.parse(match![1]) as unknown[]
  const formMeta = data[1] as unknown[]
  const items = (formMeta[1] ?? []) as unknown[][]

  // Reconstruct { entryId -> { title, required } } from each item's field def:
  // each item is [itemId, "Title", null, type, [[entryId, null, required]], ...].
  const fields = new Map<string, { title: string; required: boolean }>()
  for (const item of items) {
    const fieldDef = (item[4] as unknown[] | undefined)?.[0] as unknown[] | undefined
    if (!fieldDef) continue
    fields.set(String(fieldDef[0]), {
      title: String(item[1]),
      required: Boolean(fieldDef[2]),
    })
  }

  // The entry IDs hard-coded in Contact.svelte must still exist on the live form
  // and stay required. A regenerated/closed/deleted form breaks this mapping and
  // silently drops every submission.
  const expected: Record<string, string> = {
    '2005620554': 'Name',
    '1045781291': 'Email',
    '839337160': 'Message',
  }
  for (const [entryId, expectedTitle] of Object.entries(expected)) {
    const field = fields.get(entryId)
    expect(field, `entry.${entryId} must still exist on the live form`).toBeDefined()
    expect(field?.title, `entry.${entryId} must be the "${expectedTitle}" field`).toBe(
      expectedTitle,
    )
    expect(field?.required, `entry.${entryId} (${expectedTitle}) must stay required`).toBe(true)
  }
})
