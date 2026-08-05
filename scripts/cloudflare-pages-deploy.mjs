import { execFileSync, spawn } from 'node:child_process'

const previewFlag = process.argv.includes('--preview')
const productionFlag = process.argv.includes('--production')

const readEnv = (...names) => {
  for (const name of names) {
    const value = process.env[name]?.trim()
    if (value) {
      return value
    }
  }

  return null
}

const inferBranchName = () => {
  for (const candidate of [
    'CLOUDFLARE_PAGES_BRANCH',
    'FORGEJO_HEAD_REF',
    'FORGEJO_REF_NAME',
    'GITHUB_HEAD_REF',
    'GITHUB_REF_NAME',
  ]) {
    const value = process.env[candidate]?.trim()
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

const inferCommitHash = () =>
  readEnv('CLOUDFLARE_PAGES_COMMIT_SHA', 'FORGEJO_SHA', 'GITHUB_SHA') ?? readGit(['rev-parse', 'HEAD'])

const inferCommitMessage = () =>
  readEnv(
    'CLOUDFLARE_PAGES_COMMIT_MESSAGE',
    'FORGEJO_EVENT_HEAD_COMMIT_MESSAGE',
    'GITHUB_EVENT_HEAD_COMMIT_MESSAGE',
  ) ??
  readGit(['log', '-1', '--pretty=%s'])

const args = ['pages', 'deploy']

const productionBranch = readEnv('CLOUDFLARE_PAGES_PRODUCTION_BRANCH') ?? 'main'
const branchName = productionFlag ? productionBranch : previewFlag ? inferBranchName() : null

if (branchName) {
  args.push('--branch', branchName)
}

if (previewFlag && !branchName) {
  console.error(
    'Preview deploys need a branch name. Set CLOUDFLARE_PAGES_BRANCH or run from a git branch.',
  )
  process.exit(1)
}

if (previewFlag && branchName === productionBranch) {
  console.error(
    `Preview deploys cannot target the production branch "${productionBranch}". Choose another branch.`,
  )
  process.exit(1)
}

const commitHash = inferCommitHash()
if (commitHash) {
  args.push('--commit-hash', commitHash)
}

const commitMessage = inferCommitMessage()
if (commitMessage) {
  args.push('--commit-message', commitMessage)
}

if (readEnv('CLOUDFLARE_PAGES_COMMIT_DIRTY') === '1') {
  args.push('--commit-dirty')
}

const child = spawn('wrangler', args, {
  stdio: 'inherit',
  env: process.env,
})

child.on('exit', (code, signal) => {
  if (signal) {
    process.kill(process.pid, signal)
    return
  }

  process.exit(code ?? 1)
})
