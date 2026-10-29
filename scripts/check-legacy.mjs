import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';

const root = new URL('../', import.meta.url);
const allowances = JSON.parse(readFileSync(new URL('data/legacy-allowlist.json', root), 'utf8'));
const terms = /\b(?:documents?|collections?|feeds?|webhooks?|hrana|libsql|lattices?|leaderless|replication|turso)\b/i;
const files = execFileSync('git', ['ls-files', '-z'], { cwd: root, encoding: 'utf8' }).split('\0').filter(Boolean);
const used = new Set();
let hits = 0;
for (const path of files) {
  if (path === 'data/legacy-allowlist.json' || path === 'scripts/check-legacy.mjs') continue;
  let bytes;
  try { bytes = readFileSync(new URL(path, root)); } catch (error) {
    if (error.code === 'ENOENT') continue;
    throw error;
  }
  if (bytes.includes(0)) continue;
  const matches = bytes.toString('utf8').split('\n').filter((line) => terms.test(line));
  if (!matches.length) continue;
  const allowance = allowances[path];
  const digest = createHash('sha256').update(matches.join('\n')).digest('hex');
  assert.ok(allowance?.reason && allowance.digest === digest, `Unapproved legacy term(s): ${path}`);
  used.add(path);
  hits += matches.length;
}
for (const path of Object.keys(allowances)) assert.ok(used.has(path), `Stale legacy allowance: ${path}`);
console.log(`Legacy scan passed: ${hits} matched lines in ${used.size} explicitly allowed files; no unapproved hits.`);
