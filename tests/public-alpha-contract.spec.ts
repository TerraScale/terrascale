import { readdirSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { expect, test, type Page } from 'playwright/test'

const registrationUrl = 'https://dash.terrascale.tech/register'
const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')

const publishedRoutes = [
  { path: '/', canonical: 'https://terrascale.tech/' },
  { path: '/about', canonical: 'https://terrascale.tech/about' },
  { path: '/contact', canonical: 'https://terrascale.tech/contact' },
  { path: '/pricing', canonical: 'https://terrascale.tech/pricing' },
] as const

type PublicRoute = (typeof publishedRoutes)[number]['path']
type RenderedRoute = PublicRoute | '/404'

const renderedRoutes = [...publishedRoutes, { path: '/404', canonical: 'https://terrascale.tech/404' }] as const

const expectedRenderedCtaHrefs = {
  '/': [registrationUrl, registrationUrl, 'https://docs.terrascale.tech', registrationUrl, './contact'],
  '/about': [registrationUrl, './pricing', './contact'],
  '/contact': [registrationUrl],
  '/pricing': [
    registrationUrl,
    `${registrationUrl}?plan=starter`,
    `${registrationUrl}?plan=pro`,
    './contact',
    './contact',
    'https://docs.terrascale.tech',
    registrationUrl,
  ],
  '/404': [registrationUrl, './', './contact'],
} as const satisfies Record<RenderedRoute, readonly string[]>

const expectedRenderedRegistrationHrefs = {
  '/': [registrationUrl, registrationUrl, registrationUrl],
  '/about': [registrationUrl],
  '/contact': [registrationUrl],
  '/pricing': [
    registrationUrl,
    `${registrationUrl}?plan=starter`,
    `${registrationUrl}?plan=pro`,
    registrationUrl,
  ],
  '/404': [registrationUrl],
} as const satisfies Record<RenderedRoute, readonly string[]>

const expectedStaticHtml = ['404.html', 'about.html', 'contact.html', 'index.html', 'pricing.html']

function listStaticHtml(directory: string): string[] {
  const files: string[] = []
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const fullPath = path.join(directory, entry.name)
    if (entry.isDirectory()) files.push(...listStaticHtml(fullPath))
    else if (entry.isFile() && entry.name.endsWith('.html')) files.push(path.relative(repositoryRoot, fullPath))
  }
  return files.sort()
}

const normalizeExpectedHref = (href: string) => {
  const url = new URL(href, 'http://localhost:4173')
  return url.origin === 'http://localhost:4173' ? `${url.pathname}${url.search}${url.hash}` : url.href
}

async function assertRegistrationCtas(page: Page, path: RenderedRoute, mobile: boolean) {
  await page.setViewportSize(mobile ? { width: 390, height: 844 } : { width: 1440, height: 900 })
  await page.goto(path)

  const headerLogin = mobile
    ? page.locator('#mobile-navigation a[href="https://dash.terrascale.tech/login"]')
    : page.locator('header .ts-header__actions > a[href="https://dash.terrascale.tech/login"]')

  if (mobile) {
    const toggle = page.getByRole('button', { name: /toggle navigation/i })
    await expect(toggle).toBeVisible()
    await expect(toggle).toHaveAttribute('aria-expanded', 'false')
    await toggle.click()
    await expect(toggle).toHaveAttribute('aria-expanded', 'true')
    await expect(page.locator('#mobile-navigation')).toBeVisible()
  }
  await expect(headerLogin, `${mobile ? 'mobile' : 'desktop'} login link`).toHaveCount(1)
  await expect(headerLogin).toBeVisible()
  await expect(headerLogin).not.toHaveAttribute('href', registrationUrl)

  const semanticCtas = await page.locator('a').evaluateAll((anchors) =>
    anchors
      .map((anchor) => {
        const href = anchor.getAttribute('href') ?? ''
        const className = anchor.getAttribute('class') ?? ''
        const text = (anchor.textContent ?? '').replace(/\s+/g, ' ').trim()
        let decodedPath: string
        try {
          let candidate = new URL(href, document.baseURI).pathname
          for (let i = 0; i < 3; i += 1) {
            const next = decodeURIComponent(candidate)
            if (next === candidate) break
            candidate = next
          }
          decodedPath = candidate
        } catch {
          decodedPath = href
        }
        const registrationLike =
          /(?:^|\/)(?:register|signup)(?:\/|$)/i.test(decodedPath) ||
          /(?:register|signup|get started|sign up|start public alpha)/i.test(text) ||
          /\b(?:ts-cta|cta__primary)\b/.test(className)
        const publicCta = registrationLike || /\b(?:ts-btn|pricing-card__cta)\b/.test(className)
        const cardPlan = anchor.closest('[data-plan]')?.getAttribute('data-plan') ?? null
        let normalizedHref = `invalid:${href}`
        try {
          const resolved = new URL(href, document.baseURI)
          normalizedHref = resolved.origin === window.location.origin
            ? `${resolved.pathname}${resolved.search}${resolved.hash}`
            : resolved.href
        } catch {
          // Keep malformed CTA targets visible to the comparison below.
        }
        return {
          href,
          normalizedHref,
          registrationLike,
          publicCta,
          cardPlan,
          visible: getComputedStyle(anchor).display !== 'none' && getComputedStyle(anchor).visibility !== 'hidden',
        }
      })
      .filter(({ publicCta, registrationLike }) => publicCta || registrationLike),
  )

  const expectedCtaHrefs = [...expectedRenderedCtaHrefs[path], ...(mobile ? [registrationUrl] : [])].map(normalizeExpectedHref).sort()
  expect(semanticCtas.map(({ normalizedHref }) => normalizedHref).sort()).toEqual(expectedCtaHrefs)

  const expectedRegistrationHrefs = [...expectedRenderedRegistrationHrefs[path], ...(mobile ? [registrationUrl] : [])]
    .map(normalizeExpectedHref)
    .sort()
  expect(semanticCtas.filter(({ registrationLike }) => registrationLike).map(({ normalizedHref }) => normalizedHref).sort()).toEqual(expectedRegistrationHrefs)

  const enterpriseCtas = semanticCtas.filter(({ cardPlan }) => cardPlan === 'enterprise')
  if (path === '/pricing') expect(enterpriseCtas.map(({ normalizedHref }) => normalizedHref)).toEqual(['/contact'])
}

test('static output contains exactly the approved prerendered route set', () => {
  const actualStaticHtml = listStaticHtml(path.join(repositoryRoot, 'build'))
  expect(actualStaticHtml).toEqual(expectedStaticHtml.map((file) => path.join('build', file)).sort())
  for (const forbiddenRoute of ['security.html', 'terms.html', 'signup.html', 'login.html', 'usage.html']) {
    expect(actualStaticHtml).not.toContain(path.join('build', forbiddenRoute))
  }
})

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

test('published routes keep registration, login, status, and policy links canonical', async ({ page }) => {
  const requiredLinks = [
    registrationUrl,
    'https://dash.terrascale.tech/login',
    'https://status.terrascale.tech',
    'https://docs.terrascale.tech/legal/security/',
    'https://docs.terrascale.tech/legal/terms/',
    'https://docs.terrascale.tech/legal/terms/#acceptable-use',
    'https://docs.terrascale.tech/legal/privacy/',
    'https://docs.terrascale.tech/legal/dpa/',
    'https://docs.terrascale.tech/legal/support/',
  ]

  for (const route of publishedRoutes) {
    await page.goto(route.path)
    for (const href of requiredLinks) await expect(page.locator(`a[href="${href}"]`).first()).toBeAttached()

    const registrationLinks = page.locator(`a[href^="${registrationUrl}"]`)
    if ((await registrationLinks.count()) === 0) {
      await page.getByRole('button', { name: /toggle navigation/i }).click()
    }
    await expect(page.locator(`a[href^="${registrationUrl}"]`).first()).toBeAttached()
    await expect(page.locator('a[href*="dash.terrascale.tech/signup"]')).toHaveCount(0)
  }
})

test('every public registration CTA keeps its exact destination on desktop and mobile', async ({ page }) => {
  for (const { path } of renderedRoutes) {
    await assertRegistrationCtas(page, path, false)
    await assertRegistrationCtas(page, path, true)
  }
})

test('pricing matches the Public Alpha plan catalog', async ({ page }) => {
  await page.goto('/pricing')

  const cards = page.locator('.pricing-card')
  await expect(cards).toHaveCount(3)

  const expectedPlans = {
    starter: {
      name: 'Starter',
      price: '$5',
      limits: ['5 projects', '5 databases', '5 members', '5 GB across regions', '1,000,000 operation units/month'],
      href: `${registrationUrl}?plan=starter`,
    },
    pro: {
      name: 'Pro',
      price: '$50',
      limits: ['25 projects', '25 databases', '25 members', '25 GB across regions', '100,000,000 operation units/month'],
      href: `${registrationUrl}?plan=pro`,
    },
    enterprise: {
      name: 'Enterprise',
      price: '$500',
      limits: ['Monthly support base plus metered usage', 'Unavailable during Public Alpha'],
      href: '/contact',
    },
  }

  for (const [slug, expectedPlan] of Object.entries(expectedPlans)) {
    const card = page.locator(`.pricing-card[data-plan="${slug}"]`)
    await expect(card.getByText(expectedPlan.name, { exact: true })).toBeVisible()
    await expect(card.getByText(expectedPlan.price, { exact: true })).toBeVisible()
    for (const limit of expectedPlan.limits) {
      await expect(card.getByText(limit, { exact: true })).toBeVisible()
    }
    await expect(card.locator('a')).toHaveAttribute('href', expectedPlan.href)
  }

  await expect(page.getByText(/^(Developer|Scale|Business)$/, { exact: true })).toHaveCount(0)
  await expect(page.getByText(/Stripe/i)).toHaveCount(0)
  await expect(page.getByText(/does not offer paid overages|without paid overages/i)).toBeVisible()
  await expect(page.getByText(/30-day trial/i).first()).toBeVisible()

  const enterprise = cards.filter({ hasText: 'Enterprise' })
  await expect(enterprise.getByText(/unavailable during Public Alpha/i)).toBeVisible()
  await expect(enterprise.getByText(/contact for future access/i)).toBeVisible()
})

test('pricing billing toggle switches between monthly and annual prices', async ({ page }) => {
  await page.goto('/pricing')

  const monthly = page.getByRole('tab', { name: /monthly/i })
  const annual = page.getByRole('tab', { name: /annual/i })
  await expect(monthly).toHaveAttribute('aria-selected', 'true')
  await expect(annual).toHaveAttribute('aria-selected', 'false')

  const starter = page.locator('.pricing-card[data-plan="starter"]')
  await expect(starter.getByText('$5', { exact: true })).toBeVisible()
  await expect(starter.getByText('/month', { exact: true })).toBeVisible()

  await annual.click()
  await expect(annual).toHaveAttribute('aria-selected', 'true')
  await expect(monthly).toHaveAttribute('aria-selected', 'false')
  await expect(starter.getByText('$50', { exact: true })).toBeVisible()
  await expect(starter.getByText('/year', { exact: true })).toBeVisible()

  await monthly.click()
  await expect(monthly).toHaveAttribute('aria-selected', 'true')
  await expect(starter.getByText('$5', { exact: true })).toBeVisible()
})

test('pricing keeps every plan state usable on mobile', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('/pricing')

  const cards = page.locator('.pricing-card')
  await expect(cards).toHaveCount(3)
  await expect(page.locator('.pricing-card[data-plan="starter"] a')).toHaveAttribute('href', `${registrationUrl}?plan=starter`)
  await expect(page.locator('.pricing-card[data-plan="pro"] a')).toHaveAttribute('href', `${registrationUrl}?plan=pro`)
  await expect(page.locator('.pricing-card[data-plan="enterprise"] a')).toHaveAttribute('href', '/contact')
})

test('pricing presentation metadata stays keyed to each plan slug', async ({ page }) => {
  await page.goto('/pricing')

  const metadata = {
    starter: { icon: 'star', popular: 'false' },
    pro: { icon: 'trendingUp', popular: 'true' },
    enterprise: { icon: 'shieldCheck', popular: 'false' },
  }
  for (const [slug, expected] of Object.entries(metadata)) {
    const card = page.locator(`.pricing-card[data-plan="${slug}"]`)
    await expect(card).toHaveAttribute('data-plan-icon', expected.icon)
    await expect(card).toHaveAttribute('data-popular', expected.popular)
  }
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

  if ((await page.locator('a[href="https://dash.terrascale.tech/login"]:visible').count()) === 0) {
    await page.getByRole('button', { name: /toggle navigation/i }).click()
  }

  const expectedLinks = [
    registrationUrl,
    'https://dash.terrascale.tech/login',
    'https://status.terrascale.tech',
    'https://docs.terrascale.tech/legal/security/',
    'https://docs.terrascale.tech/legal/terms/',
    'https://docs.terrascale.tech/legal/terms/#acceptable-use',
    'https://docs.terrascale.tech/legal/privacy/',
    'https://docs.terrascale.tech/legal/dpa/',
    'https://docs.terrascale.tech/legal/support/',
  ]

  for (const href of expectedLinks) {
    await expect(page.locator(`a[href="${href}"]:visible`).first()).toBeVisible()
  }

  const policyLinks = [
    'https://docs.terrascale.tech/legal/terms/',
    'https://docs.terrascale.tech/legal/privacy/',
    'https://docs.terrascale.tech/legal/terms/#acceptable-use',
    'https://docs.terrascale.tech/legal/dpa/',
  ]
  expect(new Set(policyLinks).size).toBe(policyLinks.length)
  for (const href of policyLinks) {
    await expect(page.locator(`nav[aria-label="Registration policies"] a[href="${href}"]`)).toBeVisible()
  }
  await expect(page.locator('a[href="https://docs.terrascale.tech/legal/terms/#acceptable-use"]')).toHaveCount(1)
})

test('footer changelog and API reference links keep their canonical documentation targets', async ({ page }) => {
  const expectedFooterLinks = {
    changelog: 'https://docs.terrascale.tech/changelog/',
    apiReference: 'https://docs.terrascale.tech/getting-started/',
  }

  await page.goto('/')
  for (const href of Object.values(expectedFooterLinks)) {
    await expect(page.locator(`footer a[href="${href}"]`)).toBeVisible()
  }
  expect(expectedFooterLinks.changelog).not.toBe(expectedFooterLinks.apiReference)
  await expect(page.locator('footer a[href="https://docs.terrascale.tech"]')).toBeVisible()
})
