#!/usr/bin/env node
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const redirects = fs.readFileSync(path.join(root, 'static/_redirects'), 'utf8')
const failures = []

const requiredRules = [
  '/signup https://dash.terrascale.tech/register 308',
  'https://dashboard.terrascale.tech/* https://dash.terrascale.tech/:splat 308',
]
for (const rule of requiredRules) {
  if (!redirects.split(/\r?\n/).some((line) => line.trim() === rule)) {
    failures.push(`missing redirect rule: ${rule}`)
  }
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
