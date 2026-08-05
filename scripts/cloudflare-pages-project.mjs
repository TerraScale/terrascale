import { readFileSync } from 'node:fs'

const defaultApiBaseUrl = 'https://api.cloudflare.com/client/v4'

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

export const readRemotePagesProject = async ({ accountId, apiBaseUrl, apiToken, projectName }) => {
  if (!accountId || !apiToken) {
    throw new Error('Cloudflare project verification needs CLOUDFLARE_ACCOUNT_ID and CLOUDFLARE_API_TOKEN.')
  }

  let response
  try {
    response = await fetch(
      `${(apiBaseUrl ?? defaultApiBaseUrl).replace(/\/$/, '')}/accounts/${encodeURIComponent(accountId)}/pages/projects/${encodeURIComponent(projectName)}`,
      { headers: { Authorization: `Bearer ${apiToken}` } },
    )
  } catch {
    throw new Error(`Failed to read Cloudflare Pages project "${projectName}".`)
  }

  if (response.status === 404) {
    return null
  }

  if (!response.ok) {
    throw new Error(`Failed to read Cloudflare Pages project "${projectName}": ${response.status}.`)
  }

  try {
    return (await response.json()).result
  } catch {
    throw new Error(`Cloudflare Pages project "${projectName}" returned invalid metadata.`)
  }
}

export const verifyProductionBranch = (project, { productionBranch, projectName }) => {
  const remoteBranch = project?.production_branch

  if (!remoteBranch) {
    throw new Error(`Cloudflare Pages project "${projectName}" has no production branch.`)
  }

  if (remoteBranch !== productionBranch) {
    throw new Error(
      `Cloudflare Pages project "${projectName}" production branch mismatch: remote "${remoteBranch}", configured "${productionBranch}".`,
    )
  }

  return remoteBranch
}

export const verifyRemoteProductionBranch = async (options) =>
  verifyProductionBranch(await readRemotePagesProject(options), options)
