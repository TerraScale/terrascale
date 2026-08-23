import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import path from 'node:path'
import test from 'node:test'

import {
  checkDeployedRedirects,
  validateRedirectRules,
} from '../scripts/validate-redirect-contract.mjs'
import { probeExternalLinks } from '../scripts/validate-links.mjs'
import { validateRenderedLinks } from '../scripts/validate-links.mjs'
import { findProhibitedClaims, scanProhibitedClaims, walk as walkClaimFiles } from '../scripts/validate-claims.mjs'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const claimsValidator = path.join(root, 'scripts/validate-claims.mjs')
const claimsFixtureRoot = path.join(root, 'tests/fixtures/claims-prohibited')
const linksFixture = path.join(root, 'tests/fixtures/links-prohibited.html')
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

function assertLocalizedClaimCategories(files, fixtureRoot, label) {
  const corpus = files.map((file) => readFileSync(file, 'utf8')).join('\n')
  const normalizedCorpus = corpus.toLocaleLowerCase()
  const failures = scanProhibitedClaims(files, fixtureRoot)
  for (const category of localizedClaimCategories) {
    assert.ok(
      failures.some((failure) => failure.includes(`: ${category.id}:`)),
      `${label} scan did not report ${category.label}`,
    )
    for (const sample of category.samples) {
      assert.ok(normalizedCorpus.includes(sample.toLocaleLowerCase()), `${label} fixture is missing ${sample}`)
      assert.ok(
        findProhibitedClaims(sample).some(({ id }) => id === category.id),
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

test('localized Enterprise questions are not availability claims', () => {
  for (const question of [
    '¿Enterprise está disponible?',
    'O Enterprise está disponível?',
    'Enterprise está disponível?',
    'Enterprise is available?',
    'Enterprise remains available?',
    'Enterprise está disponible ?',
    'O Enterprise está disponível ?',
    "label: 'Enterprise está disponible ?',",
    '<p>¿Enterprise está disponible?</p>',
    '<span>O Enterprise está disponível ?</span>',
  ]) {
    assert.equal(findProhibitedClaims(question).some(({ id }) => id === 'positive-enterprise-availability'), false)
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
    assert.equal(findProhibitedClaims(declaration).some(({ id }) => id === 'positive-enterprise-availability'), true)
  }
})

test('malformed HTML never earns an Enterprise question exemption', () => {
  for (const malformed of [
    '<p class="x>Enterprise is available?</p>',
    "<p title='x>Enterprise está disponible?</p>",
    '<p data-label="Enterprise is available?',
    '<p data-label="Enterprise is available?"',
    '<p>Enterprise está disponível?</div>',
    '<p>Enterprise is available?',
    '<p>Enterprise is available?</p',
  ]) {
    assert.equal(findProhibitedClaims(malformed).some(({ id }) => id === 'positive-enterprise-availability'), true)
  }
})

test('normal rendered HTML exempts exact Enterprise FAQ nodes but not nearby claims', () => {
  const questions = ['Enterprise is available?', '¿Enterprise está disponible?', 'O Enterprise está disponível?']
  for (const document of [
    ...questions.map((question) => `<!doctype html><html><head><title>${question}</title></head><body></body></html>`),
    ...questions.map((question) => `<!doctype html><html><head></head><body><p>${question}</p></body></html>`),
    ...questions.map((question) => `<!doctype html><html><head><meta name="description" content="${question}"></head><body></body></html>`),
    '<!doctype html><html><head><meta name="description" content="x &quot; y"></head><body><p>Enterprise is available?</p></body></html>',
    '<!doctype html><html><head></head><body><p><span>Enterprise is <strong>available?</strong></span></p></body></html>',
  ]) {
    assert.equal(findProhibitedClaims(document).some(({ id }) => id === 'positive-enterprise-availability'), false)
  }

  for (const document of [
    ...questions.map((question) => `<!doctype html><html><body><p>${question}</p><p>${question} for everyone.</p></body></html>`),
    ...questions.map((question) => `<!doctype html><html><body><p>${question} Contact sales.</p></body></html>`),
    ...questions.map((question) => `<!doctype html><html><head><meta name="description" content="${question} Contact sales."></head><body></body></html>`),
    ...questions.map((question) => `<!doctype html><html><head><meta name="description" content="${question} now"></head><body><p>${question}</p></body></html>`),
    ...questions.map((question) => `<!doctype html><html><body><p>${question}<span> for everyone.</span></p></body></html>`),
    '<!doctype html><html><body><!-- Enterprise is available? --><script type="application/ld+json">{"description":"Enterprise is available?"}</script><style>.faq::after { content: "Enterprise is available?"; }</style></body></html>',
  ]) {
    assert.equal(findProhibitedClaims(document).some(({ id }) => id === 'positive-enterprise-availability'), true)
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

test('release link probes fail closed on an unavailable required endpoint', async () => {
  const failures = await probeExternalLinks(['https://status.terrascale.tech'], {
    fetchImpl: async () => new Response('unavailable', { status: 503 }),
  })

  assert.match(failures.join('\n'), /returned 503/)
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
  assertLocalizedClaimCategories(files, localizedClaimsFixtureRoot, 'rendered')
})

test('rendered link scans inspect metadata and structured-data URLs', () => {
  const result = validateRenderedLinks({ buildRoot: root, files: [linksFixture] })

  assert.match(result.failures.join('\n'), /must use HTTPS|preview or non-canonical external host/i)
})

test('the release script is explicit and separate from offline checks', () => {
  const packageJson = JSON.parse(readFileSync(path.join(root, 'package.json'), 'utf8'))
  assert.match(packageJson.scripts['check:release'], /--release/)
  assert.doesNotMatch(packageJson.scripts.check, /--release/)
})
