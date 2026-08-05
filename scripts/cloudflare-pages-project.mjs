import { readFileSync } from 'node:fs'

const cloudflareApiOrigin = 'https://api.cloudflare.com/client/v4'

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

export const readRemotePagesProject = async ({ accountId, fetchImpl = fetch, projectName, readToken }) => {
  if (!accountId || !readToken) {
    throw new Error('Cloudflare diagnostic needs CLOUDFLARE_ACCOUNT_ID and CLOUDFLARE_PAGES_READ_TOKEN.')
  }

  let response
  try {
    response = await fetchImpl(
      `${cloudflareApiOrigin}/accounts/${encodeURIComponent(accountId)}/pages/projects/${encodeURIComponent(projectName)}`,
      { headers: { Authorization: `Bearer ${readToken}` } },
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
