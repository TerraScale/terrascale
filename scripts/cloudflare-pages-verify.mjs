import { pathToFileURL } from 'node:url'
import { readPagesConfig, readRemotePagesProject } from './cloudflare-pages-project.mjs'

export const verifyPagesReadOnly = async ({ environment = process.env, fetchImpl } = {}) => {
  const { projectName } = readPagesConfig()
  if (!projectName) {
    throw new Error('Missing "name" in wrangler.toml.')
  }

  return readRemotePagesProject({
    accountId: environment.CLOUDFLARE_ACCOUNT_ID?.trim(),
    fetchImpl,
    projectName,
    readToken: environment.CLOUDFLARE_PAGES_READ_TOKEN?.trim(),
  })
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
  try {
    const project = await verifyPagesReadOnly()
    console.log(`Cloudflare Pages project "${project.name}" reports production branch "${project.production_branch}".`)
  } catch (error) {
    console.error(error.message)
    process.exit(1)
  }
}
