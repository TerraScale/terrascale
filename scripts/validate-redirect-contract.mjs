#!/usr/bin/env node
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const redirects = fs.readFileSync(path.join(root, 'static/_redirects'), 'utf8')
const failures = []
const lines = redirects
  .split(/\r?\n/)
  .map((line) => line.trim())
  .filter((line) => line && !line.startsWith('#'))

const requiredRules = [
  '/signup https://dash.terrascale.tech/register 308',
  'https://dashboard.terrascale.tech/* https://dash.terrascale.tech/:splat 308',
]
for (const rule of requiredRules) {
  if (!lines.includes(rule)) {
    failures.push(`missing redirect rule: ${rule}`)
  }
}

for (const line of lines) {
  const [, destination, status] = line.match(/^(?:\S+)\s+(\S+)\s+(\d{3})$/) ?? []
  if (!destination || !/^https:\/\//i.test(destination)) {
    failures.push(`redirect destinations must use HTTPS: ${line}`)
  }
  if (status !== '308') failures.push(`redirect migration rules must remain permanent (308): ${line}`)
}

const applyContractRedirect = (input) => {
  const url = new URL(input)
  if (url.origin === 'https://dashboard.terrascale.tech') {
    return `https://dash.terrascale.tech${url.pathname}${url.search}`
  }
  if (url.origin === 'https://terrascale.tech' && url.pathname === '/signup') {
    return `https://dash.terrascale.tech/register${url.search}`
  }
  return input
}

const redirectVectors = [
  [
    'https://terrascale.tech/signup?plan=pro',
    'https://dash.terrascale.tech/register?plan=pro',
  ],
  [
    'https://dashboard.terrascale.tech/register?probe=public-alpha',
    'https://dash.terrascale.tech/register?probe=public-alpha',
  ],
  [
    'https://dashboard.terrascale.tech/login?next=%2Fprojects',
    'https://dash.terrascale.tech/login?next=%2Fprojects',
  ],
]
for (const [input, expected] of redirectVectors) {
  const actual = applyContractRedirect(input)
  if (actual !== expected) failures.push(`redirect must preserve path and query: ${input} -> ${actual}`)
  if (applyContractRedirect(actual) !== actual) failures.push(`redirect destination must be loop-free: ${input}`)
}

if (applyContractRedirect('https://terrascale.tech/login') !== 'https://terrascale.tech/login') {
  failures.push('login must remain distinct from the signup redirect')
}

if (/dash\.terrascale\.tech\/(?:signup|usage)\b/i.test(redirects)) {
  failures.push('redirects must not use retired dashboard paths')
}

if (failures.length) {
  console.error('redirect contract validation failed:')
  for (const failure of failures) console.error(`- ${failure}`)
  process.exit(1)
}
console.log('redirect contract validation passed')
