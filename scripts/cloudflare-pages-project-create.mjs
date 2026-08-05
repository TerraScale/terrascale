import { spawn } from 'node:child_process'
import {
  readPagesConfig,
  readRemotePagesProject,
  verifyProductionBranch,
} from './cloudflare-pages-project.mjs'

const { compatibilityDate, projectName } = readPagesConfig()
const productionBranch = process.env.CLOUDFLARE_PAGES_PRODUCTION_BRANCH?.trim() || 'main'
const accountId = process.env.CLOUDFLARE_ACCOUNT_ID?.trim()
const apiToken = process.env.CLOUDFLARE_API_TOKEN?.trim()
const apiBaseUrl = process.env.CLOUDFLARE_PAGES_API_BASE_URL?.trim()

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

  try {
    const project = await readRemotePagesProject({ accountId, apiBaseUrl, apiToken, projectName })
    if (!project) {
      return false
    }

    verifyProductionBranch(project, { productionBranch, projectName })
    console.log(`Cloudflare Pages project "${projectName}" already exists.`)
    return true
  } catch (error) {
    console.error(error.message)
    process.exit(1)
  }
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
