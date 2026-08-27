#!/usr/bin/env node
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const scriptPath = fileURLToPath(import.meta.url)
const defaultRoot = path.resolve(path.dirname(scriptPath), '..')
const root = path.resolve(process.env.TERRASCALE_VALIDATOR_ROOT ?? defaultRoot)

const normalizeHostname = (hostname) => String(hostname).replace(/\.+$/, '').toLowerCase()

export const requiredRedirectRules = [
  '/signup https://dash.terrascale.tech/register 308',
  'https://dashboard.terrascale.tech/* https://dash.terrascale.tech/:splat 308',
]

export const redirectVectors = [
  {
    input: 'https://terrascale.tech/signup?plan=pro&return=%2Fpricing',
    expected: 'https://dash.terrascale.tech/register?plan=pro&return=%2Fpricing',
  },
  {
    input: 'https://dashboard.terrascale.tech/projects/acme%2Fmain?probe=public-alpha',
    expected: 'https://dash.terrascale.tech/projects/acme%2Fmain?probe=public-alpha',
  },
  {
    input: 'https://dashboard.terrascale.tech/login?next=https%3A%2F%2Fevil.example%2F',
    expected: 'https://dash.terrascale.tech/login?next=https%3A%2F%2Fevil.example%2F',
  },
]

export const parseRedirectRules = (text) =>
  text
    .split(/\r?\n/)
    .map((line, index) => ({ line: line.trim(), lineNumber: index + 1 }))
    .filter(({ line }) => line && !line.startsWith('#'))
    .map(({ line, lineNumber }) => {
      const parts = line.split(/\s+/)
      return {
        line,
        lineNumber,
        source: parts[0],
        destination: parts[1],
        status: parts[2],
        malformed: parts.length !== 3,
      }
    })

const decodePathname = (pathname) => {
  let decoded = pathname
  try {
    for (let i = 0; i < 3; i += 1) {
      const next = decodeURIComponent(decoded)
      if (next === decoded) return decoded
      decoded = next
    }
    return decoded
  } catch {
    return null
  }
}

const rawDestinationPath = (destination) => String(destination).match(/^https?:\/\/[^/?#]+([^?#]*)/i)?.[1] ?? '/'

const rawLocationPath = (location) => {
  const value = String(location).trim()
  if (/^[a-z][a-z\d+.-]*:\/\//i.test(value) || value.startsWith('//')) {
    return value.match(/^[a-z][a-z\d+.-]*:\/\/[^/?#]+([^?#]*)/i)?.[1]
      ?? value.match(/^\/\/[^/?#]+([^?#]*)/)?.[1]
      ?? '/'
  }
  return value.split(/[?#]/, 1)[0] || '/'
}

const hasUnsafeDestinationPath = (destination) => {
  const rawPath = rawDestinationPath(destination)
  const decodedPath = decodePathname(rawPath)
  if (decodedPath === null) return true
  return /%[0-9a-f]{2}/i.test(rawPath) || /(?:^|\/)\.\.?(?:\/|$)/.test(decodedPath)
}

const hasRetiredDashboardPath = (destination) => {
  try {
    const url = new URL(destination)
    if (normalizeHostname(url.hostname) !== 'dash.terrascale.tech') return false
    const decodedPath = decodePathname(url.pathname)
    return decodedPath !== null && /(?:^|\/)\b(?:signup|usage)\b(?:\/|$)/i.test(decodedPath)
  } catch {
    return false
  }
}

const validateRawLocation = (location, prefix) => {
  const decodedPath = decodePathname(rawLocationPath(location))
  if (decodedPath === null) return [`${prefix}: redirect Location has malformed encoding`]
  if (/(?:^|\/)\.\.?(?:\/|$)/.test(decodedPath)) return [`${prefix}: redirect Location contains dot-segment path traversal`]
  if (/(?:^|\/)\b(?:signup|usage)\b(?:\/|$)/i.test(decodedPath)) return [`${prefix}: redirect Location uses a retired dashboard path`]
  return []
}

const isCanonicalDestination = (destination) => {
  try {
    const url = new URL(destination)
    return url.protocol === 'https:' && normalizeHostname(url.hostname) === 'dash.terrascale.tech' && !url.port && !url.username && !url.password && !hasUnsafeDestinationPath(destination)
  } catch {
    return false
  }
}

export function validateRedirectRules(text) {
  const failures = []
  const rules = parseRedirectRules(text)
  const lines = rules.map((rule) => rule.line)

  for (const required of requiredRedirectRules) {
    if (!lines.includes(required)) failures.push(`missing redirect rule: ${required}`)
  }

  const sources = new Set()
  for (const rule of rules) {
    if (rule.malformed) {
      failures.push(`redirect rule must contain source, destination, and status: line ${rule.lineNumber}`)
      continue
    }
    if (sources.has(rule.source)) failures.push(`redirect source must be unique: ${rule.source}`)
    sources.add(rule.source)

    if ((/^https?:\/\//i.test(rule.source) || rule.source.startsWith('//')) && !/^https:\/\//i.test(rule.source)) {
      failures.push(`redirect sources must use HTTPS: ${rule.line}`)
    }
    if (!isCanonicalDestination(rule.destination)) {
      failures.push(`redirect destinations must use HTTPS and the canonical dashboard host: ${rule.line}`)
    }
    if (rule.status !== '308') failures.push(`redirect migration rules must remain permanent (308): ${rule.line}`)
  }

  if (/dash\.terrascale\.tech\/(?:signup|usage)\b/i.test(text) || rules.some(({ destination }) => hasRetiredDashboardPath(destination))) {
    failures.push('redirects must not use retired dashboard paths')
  }

  const signupRule = rules.find(({ source }) => source === '/signup')
  if (signupRule && signupRule.destination !== 'https://dash.terrascale.tech/register') {
    failures.push('signup must redirect to the canonical registration path without a splat')
  }
  const dashboardRule = rules.find(({ source }) => source === 'https://dashboard.terrascale.tech/*')
  if (dashboardRule && dashboardRule.destination !== 'https://dash.terrascale.tech/:splat') {
    failures.push('dashboard host redirects must preserve only the safe path splat')
  }

  return failures
}

export function applyContractRedirect(input) {
  if (typeof input !== 'string') return null
  let url
  try {
    url = new URL(input)
  } catch {
    return null
  }
  if (url.origin === 'https://dashboard.terrascale.tech') {
    return `https://dash.terrascale.tech${url.pathname}${url.search}`
  }
  if (url.origin === 'https://terrascale.tech' && url.pathname === '/signup') {
    return `https://dash.terrascale.tech/register${url.search}`
  }
  return input
}

export function validateRedirectVectors() {
  const failures = []
  for (const { input, expected } of redirectVectors) {
    const actual = applyContractRedirect(input)
    if (typeof actual !== 'string') {
      failures.push(`redirect vector is malformed: ${input}`)
      continue
    }
    if (actual !== expected) failures.push(`redirect must preserve the safe path and query: ${input} -> ${actual}`)
    if (!/^https:\/\//.test(actual)) failures.push(`redirect destination must use HTTPS: ${actual}`)
    if (applyContractRedirect(actual) !== actual) failures.push(`redirect destination must be loop-free: ${input}`)
    try {
      const destination = new URL(actual)
      if (destination.hostname !== 'dash.terrascale.tech') {
        failures.push(`redirect destination must use the canonical dashboard host: ${actual}`)
      }
    } catch {
      failures.push(`redirect vector produced a malformed destination: ${actual}`)
    }
  }

  if (applyContractRedirect('https://terrascale.tech/login') !== 'https://terrascale.tech/login') {
    failures.push('login must remain distinct from the signup redirect')
  }
  return failures
}

export const deployedVectors = [
  {
    label: '/signup',
    input: 'https://terrascale.tech/signup?plan=pro&return=%2Fpricing',
    destination: 'https://dash.terrascale.tech/register?plan=pro&return=%2Fpricing',
    initialStatus: 308,
  },
  {
    label: 'dashboard host',
    input: 'https://dashboard.terrascale.tech/login?next=%2Fprojects',
    destination: 'https://dash.terrascale.tech/login?next=%2Fprojects',
    initialStatus: 308,
  },
  {
    label: 'registration CTA',
    input: 'https://dash.terrascale.tech/register?plan=pro',
    destination: null,
    initialStatus: 200,
  },
]

const responseLocation = (response) => {
  if (!response?.headers) return null
  if (typeof response.headers.get === 'function') return response.headers.get('location')
  return response.headers.location ?? response.headers.Location ?? null
}

export async function checkDeployedRedirects({
  fetchImpl = globalThis.fetch,
  timeoutMs = Number(process.env.TERRASCALE_VALIDATOR_TIMEOUT_MS ?? 10_000),
  maxHops = 5,
  vectors = deployedVectors,
} = {}) {
  const failures = []
  if (typeof fetchImpl !== 'function') return ['release redirect check requires a fetch implementation']

  for (const vector of vectors) {
    let current = vector.input
    const seen = new Set()
    let completed = false
    const expectedInitialStatus = vector.initialStatus ?? 308
    const expected = vector.destination ?? vector.input

    const expectedRawFailures = validateRawLocation(expected, `${vector.label}: expected destination`)
    if (expectedRawFailures.length) {
      failures.push(...expectedRawFailures)
      continue
    }

    let expectedUrl
    try {
      expectedUrl = new URL(expected)
    } catch {
      failures.push(`${vector.label}: deployed vector has a malformed expected URL`)
      continue
    }
    const validateFinalState = (candidate, prefix) => {
      let candidateUrl
      try {
        candidateUrl = new URL(candidate)
      } catch {
        return [`${prefix}: deployed URL is malformed: ${candidate}`]
      }
      const stateFailures = []
      if (candidateUrl.protocol !== 'https:') stateFailures.push(`${prefix}: deployed URL must use HTTPS: ${candidate}`)
      if (normalizeHostname(candidateUrl.hostname) !== 'dash.terrascale.tech' || candidateUrl.port) {
        stateFailures.push(`${prefix}: deployed URL must use the canonical dashboard host: ${candidate}`)
      }
      if (candidateUrl.username || candidateUrl.password) stateFailures.push(`${prefix}: deployed URL must not contain credentials`)
      if (candidateUrl.pathname !== expectedUrl.pathname || candidateUrl.search !== expectedUrl.search) {
        stateFailures.push(`${prefix}: deployed redirect must preserve path/query state, expected ${expectedUrl.pathname}${expectedUrl.search}, received ${candidateUrl.pathname}${candidateUrl.search}`)
      }
      if (candidateUrl.hash !== expectedUrl.hash) stateFailures.push(`${prefix}: deployed redirect must preserve fragment state`)
      return stateFailures
    }

    const expectedPolicyFailures = validateFinalState(expectedUrl.href, `${vector.label}: expected destination`)
    if (expectedPolicyFailures.length) {
      failures.push(...expectedPolicyFailures)
      continue
    }

    let inputUrl
    try {
      inputUrl = new URL(vector.input)
    } catch {
      failures.push(`${vector.label}: deployed probe input is malformed`)
      continue
    }
    if (
      inputUrl.protocol !== 'https:' ||
      inputUrl.port ||
      inputUrl.username ||
      inputUrl.password ||
      !['terrascale.tech', 'dashboard.terrascale.tech', 'dash.terrascale.tech'].includes(normalizeHostname(inputUrl.hostname))
    ) {
      failures.push(`${vector.label}: deployed probe input must use an approved credential-free HTTPS host`)
      continue
    }

    for (let hop = 0; hop <= maxHops; hop += 1) {
      if (seen.has(current)) {
        failures.push(`${vector.label}: deployed redirect loop detected at ${current}`)
        completed = true
        break
      }
      seen.add(current)

      let response
      try {
        const controller = new AbortController()
        const timer = setTimeout(() => controller.abort(), timeoutMs)
        try {
          response = await fetchImpl(current, { redirect: 'manual', signal: controller.signal })
        } finally {
          clearTimeout(timer)
        }
      } catch (error) {
        failures.push(`${vector.label}: deployed redirect probe failed for ${current}: ${error instanceof Error ? error.message : String(error)}`)
        completed = true
        break
      }

      const status = response?.status
      const location = responseLocation(response)
      if (typeof status !== 'number' || !Number.isInteger(status) || status < 100 || status > 599) {
        failures.push(`${vector.label}: deployed response has a malformed or non-numeric status for ${current}`)
        completed = true
        break
      }

      if (hop === 0) {
        if (status !== expectedInitialStatus) {
          failures.push(`${vector.label}: deployed first hop must return ${expectedInitialStatus}, received ${status}`)
          completed = true
          break
        }
      }

      if (status >= 300 && status < 400) {
        if (status !== 308) {
          failures.push(`${vector.label}: deployed redirect hops must return 308, received ${status}`)
          completed = true
          break
        }
        if (!location) {
          failures.push(`${vector.label}: deployed ${status} response has no Location header`)
          completed = true
          break
        }
        const rawLocationFailures = validateRawLocation(location, `${vector.label}: deployed Location`)
        if (rawLocationFailures.length) {
          failures.push(...rawLocationFailures)
          completed = true
          break
        }
        let next
        try {
          next = new URL(location, current)
        } catch {
          failures.push(`${vector.label}: deployed Location is not a valid URL: ${location}`)
          completed = true
          break
        }
        const targetFailures = validateFinalState(next.href, `${vector.label}: deployed Location`)
        if (targetFailures.length) {
          failures.push(...targetFailures)
          completed = true
          break
        }
        current = next.href
        if (hop === maxHops) failures.push(`${vector.label}: deployed redirect chain exceeded ${maxHops} hops`)
        continue
      }

      if (status < 200 || status >= 300) failures.push(`${vector.label}: deployed chain ended with HTTP ${status} at ${current}`)
      failures.push(...validateFinalState(current, `${vector.label}: deployed final URL`))
      completed = true
      break
    }

    if (!completed) failures.push(`${vector.label}: deployed redirect check did not complete`)
  }

  return failures
}

async function main() {
  const redirectsPath = path.join(root, 'static/_redirects')
  const failures = validateRedirectRules(fs.readFileSync(redirectsPath, 'utf8'))
  failures.push(...validateRedirectVectors())

  if (process.argv.includes('--release')) {
    failures.push(...(await checkDeployedRedirects()))
  }

  if (failures.length) {
    console.error(`redirect contract validation failed${process.argv.includes('--release') ? ' (release mode)' : ''}:`)
    for (const failure of failures) console.error(`- ${failure}`)
    process.exitCode = 1
    return
  }
  console.log(`redirect contract validation passed${process.argv.includes('--release') ? ' (release mode)' : ''}`)
}

if (process.argv[1] && path.resolve(process.argv[1]) === scriptPath) await main()
