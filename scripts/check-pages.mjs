import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { appendFileSync, readFileSync, readdirSync } from 'node:fs'
import { resolve } from 'node:path'
import { pathToFileURL } from 'node:url'

export function checkProject(project) {
  assert.ok(project.name === 'ts-docs', 'Wrong Pages project')
  assert.ok(project.production_branch === 'main', 'Pages production branch must be main')
  assert.ok(!project.source, 'Use a direct-upload Pages project so builds run on Blacksmith')
  assert.ok(/^[a-z0-9-]+\.pages\.dev$/.test(project.subdomain), 'Invalid Pages subdomain')
  assert.ok(project.id, 'Missing Pages project ID')
  return { id: project.id, name: project.name, subdomain: project.subdomain }
}

export function checkDeployment(project, records, branch, commit) {
  checkProject(project)
  const deployment = records.findLast((record) => record.type === 'pages-deploy-detailed')
  assert.ok(deployment, 'Wrangler did not report a Pages deployment')
  assert.ok(deployment.pages_project === project.name, 'Wrong deployed Pages project')
  assert.ok(deployment.environment === (branch === 'main' ? 'production' : 'preview'), 'Wrong deployment environment')
  assert.ok(deployment.deployment_trigger?.metadata?.commit_hash === commit, 'Wrong deployed commit')
  assert.ok(/^[a-f0-9-]{36}$/.test(deployment.deployment_id), 'Missing Pages deployment ID')
  assert.ok(URL.canParse(deployment.url), 'Invalid Pages deployment URL')
  const url = new URL(deployment.url)
  assert.ok(url.protocol === 'https:', 'Pages deployment must use HTTPS')
  assert.ok(url.hostname.endsWith(`.${project.subdomain}`), 'Unexpected Pages deployment host')
  assert.ok(url.origin + '/' === url.href, 'Unexpected Pages deployment URL')
  return { id: deployment.deployment_id, environment: deployment.environment, url: url.origin, commit }
}

const digest = (data) => createHash('sha256').update(data).digest('hex')

export async function checkHosted(dist, origin) {
  const assets = readdirSync(resolve(dist, 'assets'))
  const search = readdirSync(resolve(dist, 'assets/chunks')).find((name) => name.startsWith('@localSearchIndexroot.'))
  assert.ok(search, 'Missing English search index')
  const app = assets.find((name) => /^app\..+\.js$/.test(name))
  const style = assets.find((name) => /^style\..+\.css$/.test(name))
  assert.ok(app && style, 'Missing application assets')
  const routes = [
    ['/', 'index.html', 'text/html'],
    ['/getting-started/javascript', 'getting-started/javascript.html', 'text/html'],
    ['/ar/getting-started/', 'ar/getting-started/index.html', 'text/html'],
    ['/guides/zero-configuration', 'guides/zero-configuration.html', 'text/html'],
    ['/examples/terrabase-js/quickstart.mjs', 'examples/terrabase-js/quickstart.mjs', 'javascript'],
    [`/assets/${app}`, `assets/${app}`, 'javascript'],
    [`/assets/${style}`, `assets/${style}`, 'text/css'],
    [`/assets/chunks/${search}`, `assets/chunks/${search}`, 'javascript'],
  ]
  for (const [route, file, contentType] of routes) {
    const response = await fetch(new URL(route, origin), { signal: AbortSignal.timeout(20_000) })
    assert.equal(response.status, 200, `${route}: expected HTTP 200`)
    assert.ok(response.headers.get('content-type')?.includes(contentType), `${route}: wrong content type`)
    assert.equal(digest(Buffer.from(await response.arrayBuffer())), digest(readFileSync(resolve(dist, file))), `${route}: hosted content differs from the tested build`)
  }
  const missing = await fetch(new URL('/__docs_deployment_missing__', origin), { signal: AbortSignal.timeout(20_000) })
  assert.equal(missing.status, 404, 'Missing routes must return HTTP 404')
  assert.equal(digest(Buffer.from(await missing.arrayBuffer())), digest(readFileSync(resolve(dist, '404.html'))), 'Missing route did not serve the VitePress 404 shell')
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const [command, projectPath, deploymentPath, branch, commit] = process.argv.slice(2)
  const project = JSON.parse(readFileSync(projectPath, 'utf8'))
  if (command === 'project') {
    console.log(JSON.stringify(checkProject(project)))
  } else {
    assert.equal(command, 'deployment', 'Expected project or deployment command')
    assert.ok(branch && commit, 'Deployment branch and commit are required')
    const records = readFileSync(deploymentPath, 'utf8').trim().split('\n').map((line) => JSON.parse(line))
    const deployment = checkDeployment(project, records, branch, commit)
    console.log(JSON.stringify(deployment))
    await checkHosted(resolve('docs/.vitepress/dist'), deployment.url)
    console.log('Hosted Pages check passed: 8 pages/assets match the tested build; custom HTTP 404 works.')
    if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY,
      `Cloudflare Pages ${deployment.environment}: [${deployment.id}](${deployment.url})\n\nCommit: \`${deployment.commit}\`\n\nEight hosted pages/assets match the audited build; custom HTTP 404 passes.\n`)
  }
}
