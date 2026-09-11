import assert from 'node:assert/strict'
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import path from 'node:path'
import { tmpdir } from 'node:os'
import test from 'node:test'

import {
  checkDeployedRedirects,
  validateRedirectRules,
} from '../scripts/validate-redirect-contract.mjs'
import { validateProductFooterLinks } from '../scripts/validate-content.mjs'
import { validateLegalPolicySource } from '../scripts/validate-legal-source.mjs'
import { extractAttributes, extractStructuredDataUrls, probeExternalLinks, requiredLinks } from '../scripts/validate-links.mjs'
import { validateRenderedLinks } from '../scripts/validate-links.mjs'
import { findProhibitedClaims, scanProhibitedClaims, walk as walkClaimFiles } from '../scripts/validate-claims.mjs'

test('required external destinations remain distinct', () => {
  assert.equal(new Set(requiredLinks).size, requiredLinks.length)
  assert.ok(requiredLinks.includes('https://docs.terrascale.tech/legal/terms/'))
  assert.ok(requiredLinks.includes('https://docs.terrascale.tech/legal/terms/#acceptable-use'))
  assert.ok(requiredLinks.includes('https://docs.terrascale.tech'))
  assert.ok(requiredLinks.includes('https://docs.terrascale.tech/changelog/'))
  assert.notEqual('https://docs.terrascale.tech/changelog/', 'https://docs.terrascale.tech')
})

test('product footer validator rejects duplicate changelog and documentation destinations', () => {
  const failures = validateProductFooterLinks(`
    export const footerColumns = [{
      key: 'product',
      links: [
        { key: 'changelog', href: 'https://docs.terrascale.tech' },
        { key: 'documentation', href: 'https://docs.terrascale.tech' },
      ],
    }]
  `)
  assert.match(failures.join('\n'), /changelog must use https:\/\/docs\.terrascale\.tech\/changelog\//)
  assert.match(failures.join('\n'), /product footer links must point to distinct canonical destinations/)
})

test('legal policy validator requires the source file and Acceptable use heading', () => {
  const fixtureRoot = mkdtempSync(path.join(tmpdir(), 'landing-policy-'))
  const termsPath = path.join(fixtureRoot, 'terms.md')
  try {
    assert.match(validateLegalPolicySource(path.join(fixtureRoot, 'missing.md')).join('\n'), /source file is missing/)
    writeFileSync(termsPath, '# Terms\n')
    assert.match(validateLegalPolicySource(termsPath).join('\n'), /missing the Acceptable use heading/)
    writeFileSync(termsPath, '# Terms\n\n## Acceptable use\n')
    assert.deepEqual(validateLegalPolicySource(termsPath), [])
  } finally {
    rmSync(fixtureRoot, { recursive: true, force: true })
  }
})

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const claimsValidator = path.join(root, 'scripts/validate-claims.mjs')
const claimsFixtureRoot = path.join(root, 'tests/fixtures/claims-prohibited')
const linksFixture = path.join(root, 'tests/fixtures/links-prohibited.html')
const malformedLinksFixture = path.join(root, 'tests/fixtures/links-malformed.html')
const whitespaceLinksFixture = path.join(root, 'tests/fixtures/links-whitespace.html')
const localizedClaimsFixtureRoot = path.join(root, 'tests/fixtures/claims-localized')

const localizedClaimCategories = [
  { id: 'unsupported-availability', label: 'availability/SLA', samples: ['Disponibilidad garantizada', 'Disponibilidade garantida'] },
  { id: 'unsupported-consistency-or-latency', label: 'zero latency', samples: ['latencia cero', 'latência zero'] },
  { id: 'unsupported-consistency-or-latency', label: 'instant replication', samples: ['Replicación instantánea', 'Replicação instantânea'] },
  { id: 'unsupported-consistency-or-latency', label: 'bounded lag', samples: ['lag acotado', 'atraso limitado'] },
  { id: 'unsupported-consistency-or-latency', label: 'majority/quorum writes', samples: ['Escrituras de mayoría', 'Escritas majoritárias', 'Escrituras de quórum', 'Escritas de quórum'] },
  { id: 'unsupported-consistency-or-latency', label: 'serve-any-region', samples: ['sirve desde cualquier región', 'atender de qualquer região'] },
  { id: 'deferred-backup-product', label: 'backups', samples: ['copias de seguridad automáticas', 'backups automáticos'] },
  { id: 'deferred-queue-product', label: 'queues', samples: ['colas', 'filas'] },
  { id: 'deferred-authentication-product', label: 'authentication product', samples: ['producto de autenticación', 'produto de autenticação'] },
  { id: 'obsolete-annual-pricing', label: 'annual billing', samples: ['Facturación anual', 'faturamento anual'] },
  { id: 'positive-paid-overages', label: 'paid overages', samples: ['Excedentes pagados disponibles', 'Excedentes pagos disponíveis'] },
  { id: 'positive-enterprise-availability', label: 'Enterprise available', samples: ['Enterprise está disponible', 'Enterprise está disponível'] },
  { id: 'obsolete-plan', label: 'Scale', samples: ['plan Scale'] },
  { id: 'obsolete-plan', label: 'Business', samples: ['plano Business'] },
]

function assertLocalizedClaimCategories(files, fixtureRoot, label, mode = 'source') {
  const corpus = files.map((file) => readFileSync(file, 'utf8')).join('\n')
  const normalizedCorpus = corpus.toLocaleLowerCase()
  const failures = scanProhibitedClaims(files, fixtureRoot, { mode })
  for (const category of localizedClaimCategories) {
    assert.ok(
      failures.some((failure) => failure.includes(`: ${category.id}:`)),
      `${label} scan did not report ${category.label}`,
    )
    for (const sample of category.samples) {
      assert.ok(normalizedCorpus.includes(sample.toLocaleLowerCase()), `${label} fixture is missing ${sample}`)
      assert.ok(
        findProhibitedClaims(sample, { mode }).some(({ id }) => id === category.id),
        `${label} validator did not reject ${sample}`,
      )
    }
  }
}

test('redirect rules reject insecure or non-permanent migrations', () => {
  const failures = validateRedirectRules(`
    /signup http://dash.terrascale.tech/register 302
    https://dashboard.terrascale.tech/* https://dash.terrascale.tech/:splat 308
  `)

  assert.ok(failures.some((failure) => /HTTPS/i.test(failure)))
  assert.ok(failures.some((failure) => /308/i.test(failure)))
})

test('redirect rules reject non-default canonical destination ports', () => {
  const failures = validateRedirectRules(`
    /signup https://dash.terrascale.tech/register 308
    https://dashboard.terrascale.tech/* https://dash.terrascale.tech/:splat 308
    https://terrascale.tech/custom https://dash.terrascale.tech:8443/custom 308
  `)

  assert.match(failures.join('\n'), /HTTPS and the canonical dashboard host/i)
})

test('redirect rules reject stale signup destinations and wrong hosts', () => {
  const failures = validateRedirectRules(`
    /signup https://dash.terrascale.tech/signup 308
    https://dashboard.terrascale.tech/* https://evil.example/:splat 308
  `)

  assert.match(failures.join('\n'), /retired dashboard paths/i)
  assert.match(failures.join('\n'), /canonical dashboard host/i)
})

test('redirect rules reject encoded and dot-segment stale destinations', () => {
  for (const destination of [
    'https://dash.terrascale.tech/%73ignup',
    'https://dash.terrascale.tech/foo/%2e%2e/signup',
    'https://dash.terrascale.tech/foo/../signup',
  ]) {
    const failures = validateRedirectRules(`
      /signup ${destination} 308
      https://dashboard.terrascale.tech/* https://dash.terrascale.tech/:splat 308
    `)

    assert.match(failures.join('\n'), /retired dashboard paths|canonical registration path|encoded|dot-segment/i, destination)
  }
})

test('deployed redirect checks verify the complete safe chain', async () => {
  const requests = []
  const responses = new Map([
    [
      'https://terrascale.tech/signup?plan=pro&return=%2Fpricing',
      new Response(null, {
        status: 308,
        headers: { location: 'https://dash.terrascale.tech/register?plan=pro&return=%2Fpricing' },
      }),
    ],
    [
      'https://dash.terrascale.tech/register?plan=pro&return=%2Fpricing',
      new Response('ok', { status: 200 }),
    ],
    [
      'https://dashboard.terrascale.tech/login?next=%2Fprojects',
      new Response(null, {
        status: 308,
        headers: { location: 'https://dash.terrascale.tech/login?next=%2Fprojects' },
      }),
    ],
    [
      'https://dash.terrascale.tech/login?next=%2Fprojects',
      new Response('ok', { status: 200 }),
    ],
    ['https://dash.terrascale.tech/register?plan=pro', new Response('ok', { status: 200 })],
  ])

  const failures = await checkDeployedRedirects({
    fetchImpl: async (url) => {
      requests.push(url)
      return responses.get(url) ?? new Response('not found', { status: 404 })
    },
  })

  assert.deepEqual(failures, [])
  assert.deepEqual(requests, [...responses.keys()])
})

test('deployed checks never fetch an insecure or noncanonical redirect target', async () => {
  const input = 'https://terrascale.tech/signup?plan=pro&return=%2Fpricing'
  for (const location of ['http://127.0.0.1:4173/loop', 'https://evil.example/loop', 'https://dash.terrascale.tech:8443/loop']) {
    const requests = []
    const failures = await checkDeployedRedirects({
      vectors: [{ label: '/signup', input, destination: 'https://dash.terrascale.tech/register?plan=pro&return=%2Fpricing', initialStatus: 308 }],
      fetchImpl: async (url) => {
        requests.push(url)
        return new Response(null, { status: 308, headers: { location } })
      },
    })

    assert.deepEqual(requests, [input])
    assert.match(failures.join('\n'), /HTTPS|canonical|rejected/i)
  }
})

test('deployed checks reject malformed statuses before following a chain', async () => {
  const input = 'https://terrascale.tech/signup?plan=pro&return=%2Fpricing'
  const requests = []
  const failures = await checkDeployedRedirects({
    vectors: [{ label: '/signup', input, destination: 'https://dash.terrascale.tech/register?plan=pro&return=%2Fpricing', initialStatus: 308 }],
    fetchImpl: async (url) => {
      requests.push(url)
      return { status: '308', headers: { location: 'https://dash.terrascale.tech/register?plan=pro&return=%2Fpricing' } }
    },
  })

  assert.equal(requests.length, 1)
  assert.match(failures.join('\n'), /status|numeric|malformed/i)
})

test('deployed checks reject wrong redirect statuses before following a chain', async () => {
  const input = 'https://terrascale.tech/signup?plan=pro&return=%2Fpricing'
  const requests = []
  const failures = await checkDeployedRedirects({
    vectors: [{ label: '/signup', input, destination: 'https://dash.terrascale.tech/register?plan=pro&return=%2Fpricing', initialStatus: 308 }],
    fetchImpl: async (url) => {
      requests.push(url)
      return new Response(null, {
        status: 302,
        headers: { location: 'https://dash.terrascale.tech/register?plan=pro&return=%2Fpricing' },
      })
    },
  })

  assert.deepEqual(requests, [input])
  assert.match(failures.join('\n'), /must return 308/i)
})

test('deployed checks fail closed on malformed vector URLs', async () => {
  const requests = []
  const failures = await checkDeployedRedirects({
    vectors: [
      { label: 'malformed input', input: 'not a URL', destination: 'https://dash.terrascale.tech/register', initialStatus: 200 },
      { label: 'malformed expected', input: 'https://terrascale.tech/signup', destination: 'not a URL', initialStatus: 308 },
    ],
    fetchImpl: async (url) => {
      requests.push(url)
      return new Response('unexpected fetch', { status: 200 })
    },
  })

  assert.deepEqual(requests, [])
  assert.match(failures.join('\n'), /malformed/i)
})

test('deployed checks always request manual redirect handling', async () => {
  const fetchOptions = []
  const failures = await checkDeployedRedirects({
    vectors: [{ label: 'registration CTA', input: 'https://dash.terrascale.tech/register?plan=pro', destination: null, initialStatus: 200 }],
    fetchImpl: async (_url, options) => {
      fetchOptions.push(options)
      return new Response('ok', { status: 200 })
    },
  })

  assert.deepEqual(failures, [])
  assert.equal(fetchOptions.length, 1)
  assert.equal(fetchOptions[0].redirect, 'manual')
})

test('deployed checks reject a wrong status on a later redirect hop', async () => {
  const input = 'https://terrascale.tech/signup?plan=pro&return=%2Fpricing'
  const destination = 'https://dash.terrascale.tech/register?plan=pro&return=%2Fpricing'
  const requests = []
  const failures = await checkDeployedRedirects({
    vectors: [{ label: '/signup', input, destination, initialStatus: 308 }],
    fetchImpl: async (url) => {
      requests.push(url)
      if (url === input) return new Response(null, { status: 308, headers: { location: destination } })
      return new Response(null, { status: 302, headers: { location: 'https://dash.terrascale.tech/login' } })
    },
  })

  assert.deepEqual(requests, [input, destination])
  assert.match(failures.join('\n'), /redirect hops must return 308/i)
})

test('deployed checks reject unsafe credentialed, preview, local, and wrong-host inputs before fetching', async () => {
  for (const input of [
    'http://terrascale.tech/signup',
    'https://user:pass@dash.terrascale.tech/register',
    'https://preview.example.invalid/register',
    'https://127.0.0.1/register',
    'https://dash.terrascale.tech:8443/register',
    'https://evil.example/register',
  ]) {
    const requests = []
    const failures = await checkDeployedRedirects({
      vectors: [{ label: 'unsafe input', input, destination: 'https://dash.terrascale.tech/register', initialStatus: 200 }],
      fetchImpl: async (url) => {
        requests.push(url)
        return new Response('unexpected fetch', { status: 200 })
      },
    })

    assert.deepEqual(requests, [], input)
    assert.match(failures.join('\n'), /approved credential-free HTTPS host/i, input)
  }
})

test('deployed checks normalize canonical trailing-dot hosts and reject noncanonical trailing-dot hosts', async () => {
  const input = 'https://terrascale.tech./signup?plan=pro'
  const destination = 'https://dash.terrascale.tech./register?plan=pro'
  const requests = []
  const failures = await checkDeployedRedirects({
    vectors: [{ label: 'trailing-dot canonical', input, destination, initialStatus: 308 }],
    fetchImpl: async (url) => {
      requests.push(url)
      if (url === input) return new Response(null, { status: 308, headers: { location: destination } })
      return new Response('ok', { status: 200 })
    },
  })

  assert.deepEqual(failures, [])
  assert.deepEqual(requests, [input, destination])

  const rejectedRequests = []
  const rejected = await checkDeployedRedirects({
    vectors: [{ label: 'trailing-dot noncanonical', input: 'https://evil.example./register', destination: 'https://dash.terrascale.tech/register', initialStatus: 200 }],
    fetchImpl: async (url) => {
      rejectedRequests.push(url)
      return new Response('unexpected fetch', { status: 200 })
    },
  })
  assert.deepEqual(rejectedRequests, [])
  assert.match(rejected.join('\n'), /approved credential-free HTTPS host/i)
})

test('deployed checks validate expected and observed destination policy', async () => {
  const expectedCredentialFailures = await checkDeployedRedirects({
    vectors: [{ label: 'credentialed expected', input: 'https://dash.terrascale.tech/register', destination: 'https://user:pass@dash.terrascale.tech/register', initialStatus: 200 }],
    fetchImpl: async () => new Response('unexpected fetch', { status: 200 }),
  })
  assert.match(expectedCredentialFailures.join('\n'), /credentials/i)

  const input = 'https://terrascale.tech/signup?plan=pro'
  const destination = 'https://dash.terrascale.tech/register?plan=pro'
  for (const location of [
    'https://user:pass@dash.terrascale.tech/register?plan=pro',
    'https://dash.terrascale.tech/foo/../register?plan=pro',
    'https://dash.terrascale.tech/%73ignup?plan=pro',
  ]) {
    const requests = []
    const failures = await checkDeployedRedirects({
      vectors: [{ label: 'unsafe observed destination', input, destination, initialStatus: 308 }],
      fetchImpl: async (url) => {
        requests.push(url)
        return new Response(null, { status: 308, headers: { location } })
      },
    })
    assert.deepEqual(requests, [input])
    assert.match(failures.join('\n'), /credentials|dot-segment|retired dashboard path/i, location)
  }
})

test('deployed checks reject path/query tampering and loops without continuing', async () => {
  const input = 'https://terrascale.tech/signup?plan=pro&return=%2Fpricing'
  const destination = 'https://dash.terrascale.tech/register?plan=pro&return=%2Fpricing'
  const requests = []
  const failures = await checkDeployedRedirects({
    vectors: [{ label: '/signup', input, destination, initialStatus: 308 }],
    fetchImpl: async (url) => {
      requests.push(url)
      if (url === input) return new Response(null, { status: 308, headers: { location: destination } })
      return new Response(null, { status: 308, headers: { location: 'https://dash.terrascale.tech/login?next=%2Felsewhere' } })
    },
  })

  assert.deepEqual(requests, [input, destination])
  assert.match(failures.join('\n'), /path\/query state/i)
})

test('deployed checks detect a canonical-host redirect loop', async () => {
  const input = 'https://terrascale.tech/signup?plan=pro&return=%2Fpricing'
  const destination = 'https://dash.terrascale.tech/register?plan=pro&return=%2Fpricing'
  const requests = []
  const failures = await checkDeployedRedirects({
    vectors: [{ label: '/signup', input, destination, initialStatus: 308 }],
    fetchImpl: async (url) => {
      requests.push(url)
      return new Response(null, { status: 308, headers: { location: destination } })
    },
  })

  assert.deepEqual(requests, [input, destination])
  assert.match(failures.join('\n'), /loop/i)
})

test('localized source scans reject every translated prohibited claim category', () => {
  const files = walkClaimFiles(path.join(localizedClaimsFixtureRoot, 'src'), new Set(['.ts']))
  assertLocalizedClaimCategories(files, localizedClaimsFixtureRoot, 'source')
})

test('Enterprise availability wording is prohibited in every source and rendered context', () => {
  for (const question of [
    '¿Enterprise está disponible?',
    'O Enterprise está disponível?',
    'Enterprise está disponível?',
    'Enterprise is available?',
    'Enterprise remains available?',
    'Enterprise available?',
    'Enterprise: is available?',
    'Enterprise está disponible ?',
    'O Enterprise está disponível ?',
    "label: 'Enterprise está disponible ?',",
    '<p>¿Enterprise está disponible?</p>',
    '<span>O Enterprise está disponível ?</span>',
  ]) {
    const mode = question.startsWith('<') ? 'rendered' : 'source'
    assert.equal(findProhibitedClaims(question, { mode }).some(({ id }) => id === 'positive-enterprise-availability'), true)
  }

  for (const declaration of [
    'Enterprise está disponible - ¿Qué incluye?',
    'Enterprise está disponible, pero no debería?',
    'Enterprise está disponível - O que inclui?',
    'O Enterprise está disponível, mas não deveria?',
    'Enterprise está disponible <strong>ahora</strong>?',
    'Enterprise está disponível [agora](./pricing)?',
    "const faq = 'Enterprise está disponible?' + ' Contacta ventas.'",
    'const faq = "O Enterprise está disponível?" + " Fale com vendas."',
    "const faq = 'Enterprise está disponible?' ' Contacta ventas.'",
    "const faq = getFaq('Enterprise está disponible?')",
    'const faq = `Enterprise está disponível? ${salesCopy}`',
    "const faq = 'Enterprise está disponible?'; emit('Contacta ventas.')",
  ]) {
    const mode = declaration.startsWith('<') ? 'rendered' : 'source'
    assert.equal(findProhibitedClaims(declaration, { mode }).some(({ id }) => id === 'positive-enterprise-availability'), true)
  }
})

test('malformed rendered markup fails closed while optional end tags remain valid', () => {
  for (const malformed of [
    '<p class="x>Enterprise is available?</p>',
    "<p title='x>Enterprise está disponible?</p>",
    '<p data-label="Enterprise is available?',
    '<p data-label="Enterprise is available?"',
    '<p>Enterprise está disponível?</div>',
    '<p>Enterprise is available?',
    '<p>Enterprise is available?</p',
    '<span>Enterprise is available?',
    '<strong>Enterprise is available?',
  ]) {
    assert.equal(findProhibitedClaims(malformed, { mode: 'rendered' }).some(({ id }) => id === 'positive-enterprise-availability'), true)
  }

  assert.deepEqual(findProhibitedClaims('<ul><li>Enterprise support<li>Contact sales</ul>', { mode: 'rendered' }), [])
})

test('rendered Enterprise availability wording is prohibited regardless of document context', () => {
  const questions = ['Enterprise is available?', '¿Enterprise está disponible?', 'O Enterprise está disponível?']
  for (const document of [
    ...questions.map((question) => `<!doctype html><html><head><title>${question}</title></head><body></body></html>`),
    ...questions.map((question) => `<!doctype html><html><head></head><body><p>${question}</p></body></html>`),
    ...questions.map((question) => `<!doctype html><html><head><meta name="description" content="${question}"></head><body></body></html>`),
    '<!doctype html><html><head><meta name="description" content="x &quot; y"></head><body><p>Enterprise is available?</p></body></html>',
    '<!doctype html><html><head></head><body><p><span>Enterprise is <strong>available?</strong></span></p></body></html>',
    '<!doctype html><html><body><h1>Pricing</h1><p>Enterprise is available?</p></body></html>',
  ]) {
    assert.equal(findProhibitedClaims(document, { mode: 'rendered' }).some(({ id }) => id === 'positive-enterprise-availability'), true)
  }

  for (const document of [
    ...questions.map((question) => `<!doctype html><html><body><p>${question}</p><p>${question} for everyone.</p></body></html>`),
    ...questions.map((question) => `<!doctype html><html><body><p>${question} Contact sales.</p></body></html>`),
    ...questions.map((question) => `<!doctype html><html><head><meta name="description" content="${question} Contact sales."></head><body></body></html>`),
    ...questions.map((question) => `<!doctype html><html><head><meta name="description" content="${question} now"></head><body><p>${question}</p></body></html>`),
    ...questions.map((question) => `<!doctype html><html><body><p>${question}<span> for everyone.</span></p></body></html>`),
    '<!doctype html><html><body><!-- Enterprise is available? --><script type="application/ld+json">{"description":"Enterprise is available?"}</script><style>.faq::after { content: "Enterprise is available?"; }</style></body></html>',
  ]) {
    assert.equal(findProhibitedClaims(document, { mode: 'rendered' }).some(({ id }) => id === 'positive-enterprise-availability'), true)
  }
})

test('rendered scans use complete decoded blocks and source text stays lexical', () => {
  for (const rendered of [
    '<p>Enterprise is available?\n<span> for everyone.</span></p>',
    '<span>Enterprise is available?<strong> for everyone.</strong></span>',
    '<p>Enterprise&nbsp;is available? for everyone.</p>',
    '<p>Enterprise&#32;is available? for everyone.</p>',
    '<p>Enterprise&#x20;is available? for everyone.</p>',
    '<p>Excedentes <strong>pagados disponibles</strong></p>',
    '<p>Excedentes <strong>pagos disponíveis</strong></p>',
    '<!--\nEnterprise is available?\n-->',
    '<script type="application/ld+json">\n{"description":"Enterprise is available?"}\n</script>',
    '<style>\n.enterprise::after { content: "Enterprise is available?"; }\n</style>',
  ]) {
    assert.equal(findProhibitedClaims(rendered, { mode: 'rendered' }).some(({ id }) => id === 'positive-enterprise-availability' || id === 'positive-paid-overages'), true)
  }

  assert.equal(findProhibitedClaims("const faq = 'Enterprise is available?'", { mode: 'source' }).some(({ id }) => id === 'positive-enterprise-availability'), true)
  assert.equal(findProhibitedClaims("const faq = 'Enterprise is available?'", { mode: 'rendered' }).some(({ id }) => id === 'positive-enterprise-availability'), true)
})

test('rendered claim streams preserve document order and raw structural claims', () => {
  for (const rendered of [
    '<span>Enterprise is available?</span><span> for everyone.</span>',
    '<span>Enterprise is</span><span> available?</span>',
    '<div>Enterprise is available?</div><div> for everyone.</div>',
    '<p>Enterprise is</p><p> available?</p>',
    '<p>Excedentes pagados</p><p> disponibles</p>',
    '<span>Enterprise is<br>available?</span>',
    '<p>Excedentes pagados<br>disponibles</p>',
  ]) {
    assert.equal(findProhibitedClaims(rendered, { mode: 'rendered' }).some(({ id }) => id === 'positive-enterprise-availability' || id === 'positive-paid-overages'), true, rendered)
  }

  assert.equal(
    findProhibitedClaims('<h1>Pricing</h1><p>Enterprise is available?</p>', { mode: 'rendered' }).some(({ id }) => id === 'positive-enterprise-availability'),
    true,
  )
  assert.equal(
    findProhibitedClaims('<a href="/terms">Terms</a>', { mode: 'rendered' }).some(({ id }) => id === 'unpublished-terms-route'),
    true,
  )
})

test('source Enterprise wording remains prohibited in complete and multiline values', () => {
  assert.equal(
    findProhibitedClaims('Enterprise is available?\nContact sales.', { mode: 'source' }).some(({ id }) => id === 'positive-enterprise-availability'),
    true,
  )
  assert.equal(
    findProhibitedClaims("const faq = 'Enterprise is available?'\nconst sales = 'Contact sales.'", { mode: 'source' }).some(({ id }) => id === 'positive-enterprise-availability'),
    true,
  )
})

test('rendered terms routes are detected from normalized href attributes', () => {
  for (const rendered of [
    '<a href="/terms">Terms</a>',
    '<a href=/terms?from=footer>Terms</a>',
    '<a href="/terms/#privacy">Terms</a>',
    '<a href="&#x2f;terms">Terms</a>',
  ]) {
    assert.equal(findProhibitedClaims(rendered, { mode: 'rendered' }).some(({ id }) => id === 'unpublished-terms-route'), true, rendered)
  }
  assert.equal(findProhibitedClaims('<a href="/terms-of-service">Terms</a>', { mode: 'rendered' }).some(({ id }) => id === 'unpublished-terms-route'), false)
})

test('claim matching decodes complete HTML entities and collapses Unicode whitespace', () => {
  for (const sample of [
    'instant&nbsp;replication',
    'instant&ensp;replication',
    'instant&emsp;replication',
    'instant&#160;replication',
    'instant&#xA0;replication',
    '<meta name="description" content="Replicación&ensp;instantánea">',
    '<script type="application/ld+json">{"description":"Replicação&emsp;instantânea"}</script>',
  ]) {
    const mode = sample.startsWith('<') ? 'rendered' : 'source'
    assert.equal(findProhibitedClaims(sample, { mode }).some(({ id }) => id === 'unsupported-consistency-or-latency'), true, sample)
  }
  assert.equal(findProhibitedClaims('<meta content="99.9&#xA0;% uptime">', { mode: 'rendered' }).some(({ id }) => id === 'unsupported-availability'), true)
  assert.equal(findProhibitedClaims('<p>instant&amp;nbsp;replication</p>', { mode: 'rendered' }).some(({ id }) => id === 'unsupported-consistency-or-latency'), false)
})

test('terms validation only considers actual href attributes and canonical TerraScale routes', () => {
  for (const sample of [
    '<a href="/terms">Terms</a>',
    '<a href="/foo/../terms/?ref=footer#privacy">Terms</a>',
    '<a href="https://terrascale.tech/terms/?ref=footer#privacy">Terms</a>',
    '<a href="https://terrascale.tech./terms">Terms</a>',
    '<a href="https://terrascale.tech:8443/terms/">Terms</a>',
    '<a href="//terrascale.tech.:8443/terms">Terms</a>',
    '<a href="//terrascale.tech/%74erms">Terms</a>',
    '<a href="/&#x74;erms">Terms</a>',
    '<a href="/%2Fterms">Terms</a>',
    '<a href="/foo/%2E%2E/terms">Terms</a>',
    '<a href=/terms?from=footer>Terms</a>',
  ]) {
    assert.equal(findProhibitedClaims(sample, { mode: 'rendered' }).some(({ id }) => id === 'unpublished-terms-route'), true, sample)
  }
  for (const sample of [
    '<a data-href="/terms">Terms</a>',
    '<p>href="/terms"</p>',
    'href="/terms"',
    '<a href="/terms-of-service">Terms</a>',
    '<a href="https://evil.example/terms">Terms</a>',
    '<a href="https://docs.terrascale.tech/terms">Terms</a>',
  ]) {
    assert.equal(findProhibitedClaims(sample, { mode: 'rendered' }).some(({ id }) => id === 'unpublished-terms-route'), false, sample)
  }
  for (const sample of [
    '<a data-href="/terms">Terms</a>',
    '<a href="/terms">Terms</a>',
    '<a href=/terms>Terms</a>',
    '<a title="quoted > marker" href="/terms">Terms</a>',
    "<a title='quoted < marker' href='/terms'>Terms</a>",
  ]) {
    const result = findProhibitedClaims(sample, { mode: 'source' }).some(({ id }) => id === 'unpublished-terms-route')
    assert.equal(result, sample.includes(' href='), sample)
  }
})

test('availability percentages are rejected in natural and localized wording', () => {
  for (const sample of ['99.9% uptime', '99,9 % disponibilidad', '99,9% disponibilidade']) {
    assert.equal(findProhibitedClaims(sample).some(({ id }) => id === 'unsupported-availability'), true, sample)
  }
})

test('neutral localized Enterprise FAQ copy contains no positive availability wording', () => {
  for (const [file, phrase] of [
    ['src/lib/i18n/messages/en.ts', 'What does the Enterprise plan include?'],
    ['src/lib/i18n/messages/es.ts', '¿Qué incluye el plan Enterprise?'],
    ['src/lib/i18n/messages/pt-br.ts', 'O que inclui o plano Enterprise?'],
  ]) {
    const source = readFileSync(path.join(root, file), 'utf8')
    assert.ok(source.includes(`q: '${phrase}'`), `${file} is missing neutral FAQ copy`)
    assert.equal(findProhibitedClaims(phrase).some(({ id }) => id === 'positive-enterprise-availability'), false, file)
  }
})

test('rendered claim scans inspect metadata and structured data', () => {
  const result = spawnSync(process.execPath, [claimsValidator, '--rendered'], {
    cwd: root,
    env: { ...process.env, TERRASCALE_VALIDATOR_ROOT: claimsFixtureRoot },
    encoding: 'utf8',
  })

  assert.notEqual(result.status, 0)
  assert.match(`${result.stdout}\n${result.stderr}`, /99\.9|zero[- ]latency|instant replication/i)
})

test('JSON-LD claim values decode JSON escapes and malformed payloads fail closed', () => {
  const escaped = String.raw`<script type="application/ld+json">{"description":"\u0069nstant\u0020replication"}</script>`
  assert.equal(findProhibitedClaims(escaped, { mode: 'rendered' }).some(({ id }) => id === 'unsupported-consistency-or-latency'), true)

  const malformed = '<script type="application/ld+json">{"description":}</script>'
  assert.equal(findProhibitedClaims(malformed, { mode: 'rendered' }).some(({ id }) => id === 'malformed-json-ld'), true)
})

test('claim and link walkers traverse template content', () => {
  const html = `
    <template>
      <p>Enterprise is available?</p>
      <a href="https://preview.example.invalid/template">preview</a>
      <script type="application/ld+json">{"url":"https://preview.example.invalid/json"}</script>
    </template>
  `
  assert.equal(findProhibitedClaims(html, { mode: 'rendered' }).some(({ id }) => id === 'positive-enterprise-availability'), true)
  assert.deepEqual(extractAttributes(html), [{ name: 'href', value: 'https://preview.example.invalid/template' }])
  assert.deepEqual(extractStructuredDataUrls(html), ['https://preview.example.invalid/json'])
})

test('release link probes fail closed on an unavailable required endpoint', async () => {
  const failures = await probeExternalLinks(['https://status.terrascale.tech'], {
    fetchImpl: async () => new Response('unavailable', { status: 503 }),
  })

  assert.match(failures.join('\n'), /returned 503/)
})

test('release link probes reject unsafe initial credential, preview, and local targets before fetching', async () => {
  for (const href of [
    'http://status.terrascale.tech',
    'https://user:pass@status.terrascale.tech',
    'https://preview.example.invalid',
    'https://localhost:4173',
    'https://127.0.0.1:4173',
  ]) {
    const requests = []
    const failures = await probeExternalLinks([href], {
      fetchImpl: async (url) => {
        requests.push(url)
        return new Response('unexpected fetch', { status: 200 })
      },
    })

    assert.deepEqual(requests, [], href)
    assert.match(failures.join('\n'), /HTTPS|credential|approved|private|loopback/i, href)
  }
})

test('release link probes reject malformed statuses and unsafe locations before fetching again', async () => {
  const unsafeLocations = [
    'http://127.0.0.1:4173/loop',
    'https://127.0.0.1/private',
    'https://user:pass@status.terrascale.tech/private',
    'https://evil.example/redirect',
    '//127.0.0.1/redirect',
  ]

  for (const location of unsafeLocations) {
    const requests = []
    const failures = await probeExternalLinks(['https://status.terrascale.tech'], {
      fetchImpl: async (url) => {
        requests.push(url)
        return new Response(null, { status: 302, headers: { location } })
      },
    })

    assert.deepEqual(requests, ['https://status.terrascale.tech'])
    assert.match(failures.join('\n'), /HTTPS|private|credential|approved|host/i)
  }

  const requests = []
  const malformedFailures = await probeExternalLinks(['https://status.terrascale.tech'], {
    fetchImpl: async (url) => {
      requests.push(url)
      return { status: '302oops', headers: { location: 'https://evil.example/redirect' } }
    },
  })

  assert.deepEqual(requests, ['https://status.terrascale.tech'])
  assert.match(malformedFailures.join('\n'), /status|numeric|malformed/i)
})

test('release link probes stop before fetching a redirect loop', async () => {
  const requests = []
  const failures = await probeExternalLinks(['https://status.terrascale.tech'], {
    fetchImpl: async (url) => {
      requests.push(url)
      return new Response(null, { status: 302, headers: { location: 'https://status.terrascale.tech' } })
    },
  })

  assert.deepEqual(requests, ['https://status.terrascale.tech'])
  assert.match(failures.join('\n'), /loop/i)
})

test('localized rendered scans inspect every metadata and JSON-LD prohibited claim category', () => {
  const files = walkClaimFiles(path.join(localizedClaimsFixtureRoot, 'build'), new Set(['.html']))
  assertLocalizedClaimCategories(files, localizedClaimsFixtureRoot, 'rendered', 'rendered')
})

test('rendered link scans inspect metadata and structured-data URLs', () => {
  const result = validateRenderedLinks({ buildRoot: root, files: [linksFixture] })

  assert.match(result.failures.join('\n'), /must use HTTPS|preview or non-canonical external host/i)
})

test('rendered link scans reject private, link-local, preview, and credential-bearing URLs', () => {
  const unsafeUrls = [
    'https://service.local/',
    'https://service.local./',
    'https://service.internal/',
    'https://service.internal./',
    'https://localhost:4173/',
    'https://localhost./',
    'https://127.0.0.1/',
    'https://127.0.0.1./',
    'https://0.0.0.0/',
    'https://10.0.0.1/',
    'https://172.16.0.1/',
    'https://192.168.0.1/',
    'https://169.254.169.254/',
    'https://[::1]/',
    'https://[::]/',
    'https://[fe80::1]/',
    'https://[::ffff:127.0.0.1]/',
    'https://[::ffff:192.168.1.1]/',
    'https://[::ffff:7f00:1]/',
    'https://preview.example.invalid/',
    'https://user:pass@status.terrascale.tech/private',
  ]
  const temporaryRoot = mkdtempSync(path.join(tmpdir(), 'terrascale-rendered-links-'))
  const fixture = path.join(temporaryRoot, 'unsafe-links.html')
  writeFileSync(fixture, unsafeUrls.map((href) => `<a href="${href}">unsafe</a>`).join('\n'))

  try {
    const result = validateRenderedLinks({ buildRoot: root, files: [fixture] })
    for (const href of unsafeUrls) assert.ok(result.failures.some((failure) => failure.includes(href)), href)
  } finally {
    rmSync(temporaryRoot, { recursive: true, force: true })
  }
})

test('rendered validation rejects protocol-relative metadata and JSON-LD URLs', () => {
  const temporaryRoot = mkdtempSync(path.join(tmpdir(), 'terrascale-protocol-relative-'))
  const fixture = path.join(temporaryRoot, 'protocol-relative.html')
  writeFileSync(fixture, `
    <a href="https://status.terrascale.tech">status</a>
    <meta property="og:url" content="//evil.example/metadata">
    <script type="application/ld+json">{"url":"//evil.example/structured"}</script>
  `)

  try {
    const result = validateRenderedLinks({ buildRoot: root, files: [fixture] })
    assert.ok(result.failures.filter((failure) => /protocol-relative links are not allowed/i.test(failure)).length >= 2)
  } finally {
    rmSync(temporaryRoot, { recursive: true, force: true })
  }
})

test('rendered external link inventory includes metadata and structured-data URLs for bounded probes', async () => {
  const temporaryRoot = mkdtempSync(path.join(tmpdir(), 'terrascale-rendered-inventory-'))
  const fixture = path.join(temporaryRoot, 'inventory.html')
  const urls = [
    'https://status.terrascale.tech',
    'https://docs.terrascale.tech/legal/security/',
    'https://docs.terrascale.tech/legal/support/',
  ]
  writeFileSync(fixture, `
    <a href="${urls[0]}">status</a>
    <meta property="og:url" content="${urls[1]}">
    <script type="application/ld+json">{"url":"${urls[2]}"}</script>
  `)

  try {
    const result = validateRenderedLinks({ buildRoot: root, files: [fixture] })
    const normalizedUrls = urls.map((href) => href.replace(/\/$/, ''))
    assert.deepEqual(new Set(normalizedUrls), new Set([...result.externalLinks].filter((href) => normalizedUrls.includes(href))))
    const requests = []
    const failures = await probeExternalLinks([...result.externalLinks], {
      allowedHosts: new Set(urls.map((href) => new URL(href).hostname)),
      fetchImpl: async (url) => {
        requests.push(url)
        return new Response('ok', { status: 200 })
      },
    })
    assert.deepEqual(failures, [])
    assert.deepEqual(new Set(requests), new Set(normalizedUrls))
  } finally {
    rmSync(temporaryRoot, { recursive: true, force: true })
  }
})

test('rendered link scans fail closed on material parse errors', () => {
  const result = validateRenderedLinks({ buildRoot: root, files: [malformedLinksFixture] })
  assert.match(result.failures.join('\n'), /malformed HTML|parse error|missing-quote/i)
})

test('metadata and JSON-LD URL checks trim surrounding whitespace', () => {
  const result = validateRenderedLinks({ buildRoot: root, files: [whitespaceLinksFixture] })
  assert.match(result.failures.join('\n'), /preview or non-canonical external host/i)
})

test('rendered link extraction uses actual parsed attributes and JSON-LD scripts', () => {
  const html = `
    <a data-href="https://preview.example.invalid/data" href=https://status.terrascale.tech>status</a>
    <form action='/contact'><img data-src="https://preview.example.invalid/data" src=/logo-principal.svg></form>
    <meta property=og:url content="https://preview.example.invalid/" />
    <script type=application/ld+json>{"url":"https://preview.example.invalid/"}</script>
  `
  assert.deepEqual(extractAttributes(html), [
    { name: 'href', value: 'https://status.terrascale.tech' },
    { name: 'action', value: '/contact' },
    { name: 'src', value: '/logo-principal.svg' },
    { name: 'content', value: 'https://preview.example.invalid/' },
  ])
  assert.deepEqual(extractStructuredDataUrls(html), ['https://preview.example.invalid/'])
})

test('the release script is explicit and separate from offline checks', () => {
  const packageJson = JSON.parse(readFileSync(path.join(root, 'package.json'), 'utf8'))
  assert.match(packageJson.scripts['check:release'], /--release/)
  assert.doesNotMatch(packageJson.scripts.check, /--release/)
})

test('the release profile runs the fail-closed release check before other release commands', () => {
  const deployConfig = JSON.parse(readFileSync(path.join(root, '.terrascale/deploy.json'), 'utf8'))
  const profile = deployConfig.releaseProfiles.find(({ name }) => name === 'release-e2e')
  assert.ok(profile)
  assert.deepEqual(profile.commands[0].arguments, ['run', 'check:release'])
})

test('entity decoding stays in validation tooling dependencies', () => {
  const packageJson = JSON.parse(readFileSync(path.join(root, 'package.json'), 'utf8'))
  assert.equal(packageJson.dependencies?.entities, undefined)
  assert.equal(packageJson.devDependencies.entities, '^8.0.0')
})
