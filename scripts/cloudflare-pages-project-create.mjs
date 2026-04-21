import { readFileSync } from 'node:fs'
import { spawn } from 'node:child_process'

const config = readFileSync(new URL('../wrangler.toml', import.meta.url), 'utf8')

const readTomlString = (key) => {
  const pattern = new RegExp(`^${key}\\s*=\\s*"([^"]+)"\\s*$`, 'm')
  return config.match(pattern)?.[1] ?? null
}

const projectName = readTomlString('name')
const compatibilityDate = readTomlString('compatibility_date')
const productionBranch = process.env.CLOUDFLARE_PAGES_PRODUCTION_BRANCH?.trim() || 'main'
const accountId = process.env.CLOUDFLARE_ACCOUNT_ID?.trim()
const apiToken = process.env.CLOUDFLARE_API_TOKEN?.trim()

if (!projectName) {
  console.error('Missing "name" in wrangler.toml.')
  process.exit(1)
}

if (!compatibilityDate) {
  console.error('Missing "compatibility_date" in wrangler.toml.')
  process.exit(1)
}

const projectExists = async () => {
  if (!accountId || !apiToken) {
    return false
  }

  const response = await fetch(
    `https://api.cloudflare.com/client/v4/accounts/${accountId}/pages/projects/${projectName}`,
    {
      headers: {
        Authorization: `Bearer ${apiToken}`,
        'Content-Type': 'application/json',
      },
    },
  )

  if (response.ok) {
    console.log(`Cloudflare Pages project "${projectName}" already exists.`)
    return true
  }

  if (response.status === 404) {
    return false
  }

  const body = await response.text()
  console.error(`Failed to check Cloudflare Pages project: ${response.status}`)
  console.error(body)
  process.exit(1)
}

if (await projectExists()) {
  process.exit(0)
}

const child = spawn(
  'wrangler',
  [
    'pages',
    'project',
    'create',
    projectName,
    '--production-branch',
    productionBranch,
    '--compatibility-date',
    compatibilityDate,
  ],
  {
    stdio: 'inherit',
    env: process.env,
  },
)

child.on('exit', (code, signal) => {
  if (signal) {
    process.kill(process.pid, signal)
    return
  }

  process.exit(code ?? 1)
})
