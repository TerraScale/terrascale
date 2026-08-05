import { execFileSync, spawn } from 'node:child_process'
import { pathToFileURL } from 'node:url'
import {
  readPagesConfig,
  rejectApiOriginOverride,
  verifyRemoteProductionBranch,
} from './cloudflare-pages-project.mjs'

const readEnv = (environment, ...names) => {
  for (const name of names) {
    const value = environment[name]?.trim()
    if (value) {
      return value
    }
  }

  return null
}

const inferBranchName = (environment) => {
  for (const candidate of [
    'CLOUDFLARE_PAGES_BRANCH',
    'FORGEJO_HEAD_REF',
    'FORGEJO_REF_NAME',
    'GITHUB_HEAD_REF',
    'GITHUB_REF_NAME',
  ]) {
    const value = environment[candidate]?.trim()
    if (value) {
      return value
    }
  }

  try {
    return execFileSync('git', ['branch', '--show-current'], { encoding: 'utf8' }).trim() || null
  } catch {
    return null
  }
}

const readGit = (args) => {
  try {
    return execFileSync('git', args, { encoding: 'utf8' }).trim() || null
  } catch {
    return null
  }
}

export const buildDeployCommand = async ({ argv = process.argv, environment = process.env, fetchImpl }) => {
  rejectApiOriginOverride(environment)
  const preview = argv.includes('--preview')
  const production = argv.includes('--production')
  const productionBranch = readEnv(environment, 'CLOUDFLARE_PAGES_PRODUCTION_BRANCH') ?? 'main'
  const branchName = production ? productionBranch : preview ? inferBranchName(environment) : null
  const { projectName } = readPagesConfig()

  if (!projectName) {
    throw new Error('Missing "name" in wrangler.toml.')
  }

  const remoteProductionBranch = await verifyRemoteProductionBranch({
    accountId: readEnv(environment, 'CLOUDFLARE_ACCOUNT_ID'),
    apiToken: readEnv(environment, 'CLOUDFLARE_API_TOKEN'),
    fetchImpl,
    productionBranch,
    projectName,
  })

  if (preview && !branchName) {
    throw new Error('Preview deploys need a branch name. Set CLOUDFLARE_PAGES_BRANCH or run from a git branch.')
  }

  if (preview && branchName === remoteProductionBranch) {
    throw new Error(
      `Preview deploys cannot target the verified production branch "${remoteProductionBranch}". Choose another branch.`,
    )
  }

  const args = ['pages', 'deploy']
  if (branchName) {
    args.push('--branch', branchName)
  }

  const commitHash = readEnv(environment, 'CLOUDFLARE_PAGES_COMMIT_SHA', 'FORGEJO_SHA', 'GITHUB_SHA') ?? readGit(['rev-parse', 'HEAD'])
  if (commitHash) {
    args.push('--commit-hash', commitHash)
  }

  const commitMessage =
    readEnv(
      environment,
      'CLOUDFLARE_PAGES_COMMIT_MESSAGE',
      'FORGEJO_EVENT_HEAD_COMMIT_MESSAGE',
      'GITHUB_EVENT_HEAD_COMMIT_MESSAGE',
    ) ?? readGit(['log', '-1', '--pretty=%s'])
  if (commitMessage) {
    args.push('--commit-message', commitMessage)
  }

  if (readEnv(environment, 'CLOUDFLARE_PAGES_COMMIT_DIRTY') === '1') {
    args.push('--commit-dirty')
  }

  return args
}

export const runDeploy = async ({ spawnImpl = spawn, ...options } = {}) =>
  spawnImpl('wrangler', await buildDeployCommand(options), { stdio: 'inherit', env: options.environment ?? process.env })

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
  try {
    const child = await runDeploy()
    child.on('exit', (code, signal) => {
      if (signal) {
        process.kill(process.pid, signal)
        return
      }

      process.exit(code ?? 1)
    })
  } catch (error) {
    console.error(error.message)
    process.exit(1)
  }
}
