import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { mkdtempSync, writeFileSync, chmodSync, existsSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'

test('real binary qualification refuses missing inputs, changed artifacts and stub mode', () => {
  const directory = mkdtempSync(join(tmpdir(), 'docs-dotnet-gate-'))
  try {
    const artifact = join(directory, 'candidate')
    const receipt = join(directory, 'receipt.json')
    const bytes = Buffer.from('not an SDK package')
    writeFileSync(artifact, bytes)
    chmodSync(artifact, 0o700)
    const sha = createHash('sha256').update(bytes).digest('hex')
    const args = ['scripts/qualify-dotnet.py', '--package', artifact, '--sha256', sha,
      '--testing-package', artifact, '--testing-sha256', sha, '--binary', artifact,
      '--binary-sha256', sha, '--version', '0.1.0-test', '--receipt', receipt]
    const run = (input) => spawnSync('python3', input, { encoding: 'utf8' })
    const missing = run(['scripts/qualify-dotnet.py'])
    assert.notEqual(missing.status, 0)
    assert.match(missing.stderr, /required/)
    const mismatched = run([...args, '--sha256', '0'.repeat(64)])
    assert.notEqual(mismatched.status, 0)
    assert.match(mismatched.stderr, /SHA-256 differs/)
    const malformed = run(args)
    assert.notEqual(malformed.status, 0)
    assert.match(malformed.stderr, /invalid SDK package/)
    const stub = run([...args, '--stub'])
    assert.notEqual(stub.status, 0)
    assert.match(stub.stderr, /unrecognized arguments/)
    assert.equal(existsSync(receipt), false)
  } finally {
    rmSync(directory, { recursive: true })
  }
})
