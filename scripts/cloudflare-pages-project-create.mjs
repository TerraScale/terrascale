import { pathToFileURL } from 'node:url'
import {
  readPagesConfig,
  rejectApiOriginOverride,
  verifyRemoteProductionBranch,
} from './cloudflare-pages-project.mjs'

export const verifyExistingProject = async ({ environment = process.env, fetchImpl } = {}) => {
  rejectApiOriginOverride(environment)
  const { compatibilityDate, projectName } = readPagesConfig()
  if (!projectName) {
    throw new Error('Missing "name" in wrangler.toml.')
  }
  if (!compatibilityDate) {
    throw new Error('Missing "compatibility_date" in wrangler.toml.')
  }

  const productionBranch = environment.CLOUDFLARE_PAGES_PRODUCTION_BRANCH?.trim() || 'main'
  await verifyRemoteProductionBranch({
    accountId: environment.CLOUDFLARE_ACCOUNT_ID?.trim(),
    apiToken: environment.CLOUDFLARE_API_TOKEN?.trim(),
    fetchImpl,
    productionBranch,
    projectName,
  })
  return projectName
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
  try {
    const projectName = await verifyExistingProject()
    console.log(`Cloudflare Pages project "${projectName}" already exists and matches the configured production branch.`)
  } catch (error) {
    console.error(error.message)
    process.exit(1)
  }
}
