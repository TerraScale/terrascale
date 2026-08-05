import { cpSync, existsSync, mkdirSync, rmSync } from 'node:fs'
import { pathToFileURL } from 'node:url'

export const stagePagesCandidate = ({ buildDirectory = 'build', candidateDirectory = 'artifacts/candidate' } = {}) => {
  if (!existsSync(`${buildDirectory}/index.html`)) {
    throw new Error(`Missing static build entry point: ${buildDirectory}/index.html`)
  }

  const siteDirectory = `${candidateDirectory}/site`
  rmSync(siteDirectory, { force: true, recursive: true })
  mkdirSync(candidateDirectory, { recursive: true })
  cpSync(buildDirectory, siteDirectory, { recursive: true })
  return siteDirectory
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
  try {
    console.log(`Staged static site at ${stagePagesCandidate()}.`)
  } catch (error) {
    console.error(error.message)
    process.exit(1)
  }
}
