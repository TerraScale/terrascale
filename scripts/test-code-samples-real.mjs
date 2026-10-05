import assert from 'node:assert/strict'
import { createHash, randomBytes } from 'node:crypto'
import { createReadStream, mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import path from 'node:path'
import { createServer } from 'node:net'
import { request as httpsRequest } from 'node:https'
import { spawn, execFile } from 'node:child_process'
import { promisify } from 'node:util'
import { setTimeout as delay } from 'node:timers/promises'
import { once } from 'node:events'
import ts from 'typescript'
import { testCsharpSample } from './test-csharp-sample.mjs'

const exec = promisify(execFile)

const release = 'ci-ff256067015433c8c40b59879f1282d2a40480ce-run-13258'
const digest = 'e07d173fc2bbab19dcb1fde8b121b0207b07d55452a5b19a406871d220e05e13'
const binary = process.env.TERRABASE_TEST_BINARY
assert.ok(binary, `Set TERRABASE_TEST_BINARY to ${release}/terrabase-linux-amd64`)
const hash = createHash('sha256')
for await (const chunk of createReadStream(binary)) hash.update(chunk)
assert.equal(hash.digest('hex'), digest, 'TerraBase release checksum mismatch')

async function freePort() {
  const server = createServer()
  server.listen(0, '127.0.0.1')
  await once(server, 'listening')
  const port = server.address().port
  await new Promise((resolve) => server.close(resolve))
  return port
}

const directory = mkdtempSync(path.join(tmpdir(), 'ts502-real-'))
const certificate = path.join(directory, 'certificate.pem')
const privateKey = path.join(directory, 'private-key.pem')
const credential = randomBytes(32).toString('hex')
let server
let diagnostics = ''

async function post(url, headers, body) {
  return new Promise((resolve, reject) => {
    const req = httpsRequest(url, { method: 'POST', ca: readFileSync(certificate),
      headers: { 'content-type': 'application/json', ...headers }, timeout: 5000 }, (response) => {
      response.on('error', reject)
      response.on('aborted', () => reject(new Error('Local TerraBase response aborted')))
      let text = ''
      response.on('data', (chunk) => { text += chunk })
      response.on('end', () => {
        try { assert.equal(response.statusCode, 201, text); resolve(JSON.parse(text)) } catch (error) { reject(error) }
      })
    })
    req.on('error', reject)
    req.on('timeout', () => req.destroy(new Error('Local TerraBase request timed out')))
    req.end(JSON.stringify(body))
  })
}

try {
  writeFileSync(path.join(directory, 'credential'), credential, { mode: 0o600 })
  await exec('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes',
    '-keyout', privateKey, '-out', certificate, '-days', '1', '-subj', '/CN=localhost',
    '-addext', 'subjectAltName=DNS:localhost,IP:127.0.0.1'], { encoding: 'utf8', timeout: 10_000, killSignal: 'SIGKILL' })
  const clientPort = await freePort()
  let adminPort = await freePort()
  while (adminPort === clientPort) adminPort = await freePort()
  const origin = `https://localhost:${clientPort}`
  const admin = `http://127.0.0.1:${adminPort}`
  server = spawn(path.resolve(binary), [`--data-directory=${directory}/data`, 
    `--client-port=${clientPort}`, `--admin-port=${adminPort}`, `--client-certificate=${certificate}`,
    `--client-private-key=${privateKey}`, `--dashboard-credential-file=${directory}/credential`],
  { stdio: ['ignore', 'pipe', 'pipe'], env: { PATH: process.env.PATH, DOTNET_ENVIRONMENT: 'Production', DOTNET_BUNDLE_EXTRACT_BASE_DIR: `${directory}/bundle` } })
  for (const stream of [server.stdout, server.stderr]) {
    stream.on('data', chunk => { diagnostics = (diagnostics + chunk).slice(-16_384) })
  }
  let startupError
  server.on('error', (error) => { startupError = error })
  let ready = false
  for (let attempt = 0; attempt < 100; attempt++) {
    assert.ifError(startupError)
    assert.equal(server.exitCode, null, 'TerraBase exited before readiness: ' + diagnostics)
    assert.equal(server.signalCode, null, 'TerraBase was killed before readiness')
    try {
      const health = await fetch(`${admin}/healthz`, { signal: AbortSignal.timeout(500) })
      ready = health.ok
      await health.body?.cancel()
    } catch { /* startup */ }
    if (ready) break
    await delay(100)
  }
  assert.ok(ready, 'Local TerraBase did not become ready: ' + diagnostics)
  const management = { Authorization: `Bearer ${credential}`, 'X-TerraBase-User-Id': 'ts502-test' }
  const organization = await post(`${origin}/v1/management/organizations`, management, { name: 'TS502' })
  management['X-TerraBase-Organization-Id'] = organization.id
  const project = await post(`${origin}/v1/management/organizations/${organization.id}/projects`, management, { name: 'Samples' })
  await post(`${origin}/v1/management/organizations/${organization.id}/projects/${project.id}/databases`, management, { name: 'samples' })
  const issueKey = async (scopes) => {
    const response = await fetch(`${admin}/admin/api-keys`, { method: 'POST', signal: AbortSignal.timeout(5000),
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ name: 'TS502', scopes, organizationId: organization.id }) })
    const body = await response.json()
    assert.equal(response.status, 201)
    return body.secret
  }
  const fullKey = await issueKey(['documents:read', 'documents:write'])
  const writeKey = await issueKey(['documents:write'])
  const source = ts.transpileModule(readFileSync('src/lib/highlight.ts', 'utf8'),
    { compilerOptions: { module: ts.ModuleKind.ESNext } }).outputText
  const { CODE_SAMPLES } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`)
  const code = ts.transpileModule(CODE_SAMPLES.typescript, { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText
  for (const [name, apiKey, expectedError, count] of [
    ['success', fullKey, null, 2], ['create_failure', 'tb_invalid', 'Create failed: 401', 1],
    ['query_failure', writeKey, 'Query failed: 403', 2],
  ]) {
    await post(`${origin}/v1/projects/${project.id}/databases/samples/collections`,
      { Authorization: `Bearer ${fullKey}` }, { name })
    const script = `
      import assert from 'node:assert/strict';
      const trace = [];
      const realFetch = globalThis.fetch;
      globalThis.fetch = async (url, options) => {
        assert.equal(options.redirect, 'error');
        assert.ok(options.signal instanceof AbortSignal);
        assert.equal(options.signal.aborted, false);
        assert.equal(options.headers.Authorization, 'Bearer ' + process.env.TERRABASE_API_KEY);
        const response = await realFetch(url, options);
        const entry = { path: url.pathname, cancelled: false };
        trace.push(entry);
        if (response.body) {
          const cancel = response.body.cancel.bind(response.body);
          response.body.cancel = async (...args) => { entry.cancelled = true; return cancel(...args); };
        }
        return response;
      };
      ${code}
      let value, error;
      try {
        value = await createAndQueryCustomers(() => { throw new Error('Override must skip discovery'); },
          [${JSON.stringify(origin)}], ${JSON.stringify(project.id)}, 'samples', ${JSON.stringify(name)}, ${JSON.stringify(origin)});
      } catch (caught) { error = caught.message; }
      assert.equal(error ?? null, ${JSON.stringify(expectedError)});
      assert.equal(trace.length, ${count});
      assert.ok(trace[0].path.endsWith('/documents'));
      assert.equal(trace[0].cancelled, true);
      if (${count} === 2) assert.ok(trace[1].path.endsWith('/query'));
      if (${JSON.stringify(name)} === 'query_failure') assert.equal(trace[1].cancelled, true);
      if (${JSON.stringify(name)} === 'success') {
        assert.ok(value.items.some(item => item.document.email === 'team@example.com' && item.document.plan === 'pro'));
      }
    `
    await exec(process.execPath, ['--input-type=module', '-e', script], { encoding: 'utf8',
      timeout: 10_000, killSignal: 'SIGKILL', env: { TERRABASE_API_KEY: apiKey, NODE_EXTRA_CA_CERTS: certificate } })
    console.log(`${name}: real TerraBase requests, headers, redirect options and cancellation passed`)
  }
  await testCsharpSample({ code: CODE_SAMPLES.csharp, directory, origin, project: project.id, fullKey, writeKey, certificate })
  console.log(`Verified release ${release} (${digest})`)
} finally {
  if (server?.pid && server.exitCode === null && server.signalCode === null) {
    const exited = once(server, 'exit')
    server.kill('SIGTERM')
    const timer = setTimeout(() => server.kill('SIGKILL'), 5000)
    await exited
    clearTimeout(timer)
  }
  rmSync(directory, { recursive: true, force: true })
}
