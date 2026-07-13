import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const failures = []
const extensions = new Set(['.svelte', '.ts'])
const forbidden = [
  [/terrascale\.(?:com|io)\b/i, 'Use the canonical terrascale.tech domain.'],
  [/discord\.gg\/(?:terrascale|your-|placeholder|invite-link)/i, 'Use the configured Discord invite.'],
  [/\b(?:thousands of|miles de|milhares de)\b/i, 'Unverified customer-count claims are prohibited.'],
  [/\b(?:99\.9+%|support SLA|SLA de soporte|SLA de suporte)\b/i, 'Unapproved availability or support SLA claims are prohibited.'],
  [/\b(?:Save|Ahorra|Economize)\s*20\s*%/i, 'Annual pricing must say two months free.'],
  [/href=["']\/terms["']/i, 'The terms route must remain unpublished until approved.'],
]

function* walk(directory) {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const full = path.join(directory, entry.name)
    if (entry.isDirectory()) yield* walk(full)
    else if (extensions.has(path.extname(entry.name))) yield full
  }
}

for (const file of walk(path.join(root, 'src'))) {
  const relative = path.relative(root, file)
  const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/)
  for (const [pattern, reason] of forbidden) {
    lines.forEach((line, index) => {
      if (pattern.test(line)) failures.push(`${relative}:${index + 1}: ${reason}`)
    })
  }
}

const pricing = fs.readFileSync(path.join(root, 'src/components/sections/PricingSection.svelte'), 'utf8')
for (const match of pricing.matchAll(/name:\s*'([^']+)'[\s\S]*?price:\s*\{\s*monthly:\s*(\d+),\s*annual:\s*(\d+)\s*\}/g)) {
  const [, plan, monthlyText, annualText] = match
  if (Number(annualText) !== Number(monthlyText) * 10) {
    failures.push(`PricingSection.svelte: ${plan} annual price must be ten monthly payments (two months free).`)
  }
}

const expectedRoutes = ['/', '/about', '/contact', '/pricing']
const sitemap = fs.readFileSync(path.join(root, 'src/routes/sitemap.xml/+server.ts'), 'utf8')
for (const route of expectedRoutes) {
  if (!sitemap.includes(`'${route}'`)) failures.push(`sitemap.xml: missing published route ${route}`)
}
if (fs.existsSync(path.join(root, 'src/routes/terms/+page.svelte'))) {
  failures.push('src/routes/terms/+page.svelte: terms must remain unpublished.')
}

if (failures.length) {
  console.error('Landing content validation failed:')
  failures.forEach(failure => console.error(`- ${failure}`))
  process.exit(1)
}
console.log('landing content validation passed')
