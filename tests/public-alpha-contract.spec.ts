import { expect, test } from 'playwright/test'

const registrationUrl = 'https://dash.terrascale.tech/register'

const publishedRoutes = [
  { path: '/', canonical: 'https://terrascale.tech/' },
  { path: '/about', canonical: 'https://terrascale.tech/about' },
  { path: '/contact', canonical: 'https://terrascale.tech/contact' },
  { path: '/pricing', canonical: 'https://terrascale.tech/pricing' },
]

test('published routes expose canonical metadata and basic accessible structure', async ({ page }) => {
  for (const route of publishedRoutes) {
    await page.goto(route.path)

    await expect(page.locator('link[rel="canonical"]')).toHaveAttribute('href', route.canonical)
    await expect(page.locator('meta[property="og:url"]')).toHaveAttribute('content', route.canonical)
    await expect(page.locator('meta[property="og:type"]')).toHaveAttribute('content', 'website')
    await expect(page.locator('meta[name="description"]')).toHaveAttribute('content', /\S+/)
    await expect(page.locator('meta[property="og:title"]')).toHaveAttribute('content', /\S+/)
    await expect(page.locator('meta[property="og:description"]')).toHaveAttribute('content', /\S+/)
    await expect(page.locator('h1')).toHaveCount(1)

    const inaccessibleImages = await page.locator('img').evaluateAll((images) =>
      images.filter((image) => !image.hasAttribute('alt')).length,
    )
    expect(inaccessibleImages).toBe(0)

    const inaccessibleLinks = await page.locator('a:visible').evaluateAll((links) =>
      links.filter((link) => {
        const label = link.getAttribute('aria-label') ?? link.textContent ?? ''
        return !link.getAttribute('href') || !label.trim()
      }).length,
    )
    expect(inaccessibleLinks).toBe(0)
  }
})

test('pricing matches the Public Alpha plan catalog', async ({ page }) => {
  await page.goto('/pricing')

  const cards = page.locator('.pricing-card')
  await expect(cards).toHaveCount(4)

  const expectedPlans = [
    {
      name: 'Developer',
      price: '$0',
      limits: ['1 project', '3 databases', '3 members', '1 GB across regions', '10,000 operation units/month'],
      href: `${registrationUrl}?plan=developer`,
    },
    {
      name: 'Starter',
      price: '$5',
      limits: ['5 projects', '10 databases', '5 members', '5 GB across regions', '1,000,000 operation units/month'],
      href: `${registrationUrl}?plan=starter`,
    },
    {
      name: 'Pro',
      price: '$25',
      limits: ['25 projects', '50 databases', '25 members', '50 GB across regions', '10,000,000 operation units/month'],
      href: `${registrationUrl}?plan=pro`,
    },
    {
      name: 'Enterprise',
      price: '$500',
      limits: ['Monthly support base plus metered usage', 'Unavailable during Public Alpha'],
      href: '/contact',
    },
  ]

  for (const [index, expectedPlan] of expectedPlans.entries()) {
    const card = cards.nth(index)
    await expect(card.getByText(expectedPlan.name, { exact: true })).toBeVisible()
    await expect(card.getByText(expectedPlan.price, { exact: true })).toBeVisible()
    for (const limit of expectedPlan.limits) {
      await expect(card.getByText(limit, { exact: true })).toBeVisible()
    }
    await expect(card.locator('a')).toHaveAttribute('href', expectedPlan.href)
  }

  await expect(page.getByText(/^(Scale|Business)$/, { exact: true })).toHaveCount(0)
  await expect(page.getByText(/annual|Stripe/i)).toHaveCount(0)
  await expect(page.getByText(/does not offer paid overages|without paid overages/i)).toBeVisible()
  await expect(page.getByText(/non-expiring/i).first()).toBeVisible()
  await expect(page.getByText(/30-day trial/i).first()).toBeVisible()
})

test('Public Alpha claims and customer links stay within the launch contract', async ({ page }) => {
  await page.goto('/')

  await expect(page.getByRole('heading', { level: 1 })).toContainText('Public Alpha')
  await expect(page.getByText(/São Paulo/).first()).toBeVisible()
  await expect(page.getByText(/San Francisco/).first()).toBeVisible()
  await expect(page.getByText(/Falkenstein/).first()).toBeVisible()
  await expect(page.getByText(/asynchronous replication/i).first()).toBeVisible()
  await expect(page.getByText(/no uptime percentage or SLA/i).first()).toBeVisible()
  await expect(page.getByText(/Customer content stays in selected regions/i)).toBeVisible()
  await expect(page.getByText(/TLS 1\.2 or newer, data at rest uses qualified encryption/i)).toBeVisible()
  await expect(page.getByText(/every request still requires authorization/i)).toBeVisible()

  const body = await page.locator('body').innerText()
  expect(body).not.toMatch(/zero[- ]latency|instant replication|bounded lag|majority write|serve from any region/i)
  expect(body).not.toMatch(/\b(?:queue|queues|authentication product|backup|backups|annual billing)\b/i)
  expect(body).not.toMatch(/paid overages? (?:are|remain|supported|available)/i)

  const expectedLinks = [
    registrationUrl,
    'https://status.terrascale.tech',
    'https://docs.terrascale.tech/en/security/',
    'https://docs.terrascale.tech/en/legal/terms/',
    'https://docs.terrascale.tech/en/legal/privacy/',
    'https://docs.terrascale.tech/en/legal/acceptable-use/',
    'https://docs.terrascale.tech/en/support/',
  ]

  for (const href of expectedLinks) {
    await expect(page.locator(`a[href="${href}"]:visible`).first()).toBeVisible()
  }
})
