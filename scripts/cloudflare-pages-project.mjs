import { readFileSync } from 'node:fs'

const cloudflareApiOrigin = 'https://api.cloudflare.com/client/v4'

export const rejectApiOriginOverride = (environment) => {
  if (environment.CLOUDFLARE_PAGES_API_BASE_URL?.trim()) {
    throw new Error('CLOUDFLARE_PAGES_API_BASE_URL is not supported; Pages metadata uses the fixed Cloudflare API origin.')
  }
}

export const readPagesConfig = () => {
  const config = readFileSync(new URL('../wrangler.toml', import.meta.url), 'utf8')
  const readTomlString = (key) => {
    const pattern = new RegExp(`^${key}\\s*=\\s*"([^"]+)"\\s*$`, 'm')
    return config.match(pattern)?.[1] ?? null
  }

  return {
    compatibilityDate: readTomlString('compatibility_date'),
    projectName: readTomlString('name'),
  }
}

export const readRemotePagesProject = async ({ accountId, apiToken, fetchImpl = fetch, projectName }) => {
  if (!accountId || !apiToken) {
    throw new Error('Cloudflare project verification needs CLOUDFLARE_ACCOUNT_ID and CLOUDFLARE_API_TOKEN.')
  }

  let response
  try {
    response = await fetchImpl(
      `${cloudflareApiOrigin}/accounts/${encodeURIComponent(accountId)}/pages/projects/${encodeURIComponent(projectName)}`,
      { headers: { Authorization: `Bearer ${apiToken}` } },
    )
  } catch {
    throw new Error(`Failed to read Cloudflare Pages project "${projectName}".`)
  }

  if (response.status === 404) {
    throw new Error(`Cloudflare Pages project "${projectName}" was not found.`)
  }

  if (!response.ok) {
    throw new Error(`Failed to read Cloudflare Pages project "${projectName}": ${response.status}.`)
  }

  let envelope
  try {
    envelope = await response.json()
  } catch {
    throw new Error(`Cloudflare Pages project "${projectName}" returned invalid metadata.`)
  }

  const project = envelope?.result
  if (
    envelope?.success !== true ||
    !project ||
    project.name !== projectName ||
    typeof project.production_branch !== 'string' ||
    !project.production_branch.trim()
  ) {
    throw new Error(`Cloudflare Pages project "${projectName}" returned invalid metadata.`)
  }

  return project
}

export const verifyRemoteProductionBranch = async (options) => {
  const project = await readRemotePagesProject(options)
  if (project.production_branch !== options.productionBranch) {
    throw new Error(
      `Cloudflare Pages project "${options.projectName}" production branch mismatch: remote "${project.production_branch}", configured "${options.productionBranch}".`,
    )
  }

  return project.production_branch
}
