import { execFileSync } from 'node:child_process'
import { writeFile } from 'node:fs/promises'

const revision = execFileSync('git', ['rev-parse', 'HEAD'], {
  cwd: new URL('..', import.meta.url),
  encoding: 'utf8',
}).trim()

await writeFile(
  new URL('../build/build-info.json', import.meta.url),
  `${JSON.stringify({ revision, deploymentSample: 2 })}\n`,
)
