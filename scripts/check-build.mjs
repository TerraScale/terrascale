import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { canonicalEnvelope, runAll } from './run-snippets.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const read = (path) => readFileSync(join(root, path), 'utf8');
// Preserve the exact qualification boundary of the unchanged C# consumer.
const receipt = JSON.parse(read('examples/terrabase-dotnet/qualification.json'));
assert.equal(receipt.status, 'PASS_REAL_TERRABASE_STARTER');
for (const [source, expected] of Object.entries(receipt.source_sha256)) {
  assert.equal(createHash('sha256').update(readFileSync(join(root, source))).digest('hex'), expected,
    `${source} changed; fresh exact-artifact qualification is required`);
}
for (const [script, args] of [
  ['canonical-data.mjs', ['--check']],
  ['generate-pricing.mjs', ['--check']],
  ['generate-llms.mjs', ['--check']],
  ['i18n-sync.mjs', ['--check']],
  ['check-public.mjs', []],
]) {
  const result = spawnSync(process.execPath, [join(root, 'scripts', script), ...args], { cwd: root, encoding: 'utf8' });
  assert.equal(result.status, 0, result.stdout + result.stderr);
  process.stdout.write(result.stdout);
}
const stub = process.argv.includes('--sandbox=stub');
assert.ok(!(stub && process.argv.includes('--sandbox=remote')), 'Choose one sandbox mode');
const results = await runAll(stub ? 'stub' : 'remote');
if (stub) console.warn('LOCAL-ONLY: retained legacy fixture checks do not qualify current SDKs or the hosted service.');
for (const name of ['crud.ts', 'sql.ts', 'subscribe.ts']) {
  assert.ok(results.some(({ file }) => file === name), `${name} ran`);
}
for (const name of ['tenants.ts', 'branches.ts', 'backups.ts', 'exports.ts']) {
  assert.ok(!results.some(({ file }) => file === name), `${name} must remain unqualified`);
}
const heartbeat = Uint8Array.from([0x97, 0x01, 0x00, 0x00, 0x06, 0xcf, 0, 0, 0, 0, 0, 0, 0, 0, 0xcf, 0, 0, 0, 0, 0, 0, 0, 0, 0x90]);
assert.equal(canonicalEnvelope(heartbeat).kind, 'heartbeat');
assert.throws(() => canonicalEnvelope(Uint8Array.from([0x81, 0xa1, 0x74, 0xa5, 0xa5, 0x64, 0x65, 0x6c, 0x74, 0x61])), /indexed envelope|trailing bytes|maps are forbidden/u);
console.log(`Documentation checks and ${results.length} retained legacy fixture checks passed.`);
