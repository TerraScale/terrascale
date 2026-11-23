import { readFile, writeFile, rename, realpath } from 'node:fs/promises';
import { resolve, sep } from 'node:path';
import { createHash } from 'node:crypto';

process.on('uncaughtException', () => {
  console.error('Delta stopped; reconcile the delta journal before another apply; do not log credentials or signed URLs');
  process.exitCode = 1;
});
const fail = message => { console.error(message); throw new Error('Delta stopped'); };
const [previousPath, finalPath, rootPath, statePath, mode] = process.argv.slice(2);
if (!previousPath || !finalPath || !rootPath || !statePath || !['--dry-run', '--apply'].includes(mode)) {
  fail('Usage: node retire-delta.mjs previous.json final.json staging state.json --dry-run|--apply');
}
const readJson = async path => JSON.parse(await readFile(path, 'utf8'));
const previous = await readJson(previousPath);
const final = await readJson(finalPath);
const state = await readJson(statePath);
const root = await realpath(rootPath);
const keyOf = item => JSON.stringify([item.bucket, item.name]);
const indexed = items => {
  if (!Array.isArray(items)) fail('Manifest must be an array');
  const result = new Map();
  for (const item of items) {
    if (!item.bucket || !item.name || !item.bucketId || !item.contentType || !item.userId) fail('Missing manifest field');
    const key = keyOf(item);
    if (result.has(key)) fail('Duplicate source object');
    result.set(key, item);
  }
  return result;
};
const old = indexed(previous);
const current = indexed(final);
if (Object.keys(state.files).length !== old.size) fail('Previous manifest and checkpoint differ');
for (const [key, item] of old) {
  const saved = state.files[key];
  if (!saved || saved.phase !== 'verified' || !saved.id || saved.bucketId !== item.bucketId || saved.size !== item.size) fail('Reconcile the previous checkpoint first');
}
const hashes = new Map();
for (const [key, item] of current) {
  if (typeof item.userId !== 'string' || !/^[0-7][0-9A-HJKMNP-TV-Z]{25}$/.test(item.userId)) fail('Target owner must be a canonical ULID');
  const path = await realpath(resolve(root, item.bucket, item.name));
  if (!path.startsWith(root + sep)) fail('Local path leaves staging');
  const bytes = await readFile(path);
  if (bytes.length !== item.size || bytes.length < 1 || bytes.length > 104857600) fail('Source size mismatch or unsupported size');
  hashes.set(key, createHash('sha256').update(bytes).digest('base64'));
}
const retired = [];
const nextState = structuredClone(state);
for (const [key, item] of old) {
  const next = current.get(key);
  const saved = state.files[key];
  if (!next || saved.sha256 !== hashes.get(key) || saved.bucketId !== next.bucketId || item.contentType !== next.contentType || item.userId !== next.userId) {
    retired.push({ key, ...saved, reason: next ? 'replace' : 'delete' });
    delete nextState.files[key];
  }
}
console.log(`Delta: ${retired.filter(item => item.reason === 'replace').length} replacements, ${retired.filter(item => item.reason === 'delete').length} deletions, ${[...current.keys()].filter(key => !old.has(key)).length} additions, ${old.size - retired.length} unchanged`);
if (mode === '--dry-run') {
  console.log('Dry run: no network requests or checkpoint changes');
  process.exit(0);
}
const required = name => process.env[name] || fail(`Missing ${name}`);
const project = required('TERRASCALE_PROJECT_ID');
if (state.project !== project) fail('Checkpoint belongs to another project');
const journalPath = `${statePath}.delta.json`;
try { await readFile(journalPath); fail('Existing delta journal: reconcile or archive it first'); }
catch (error) { if (error.code !== 'ENOENT') throw error; }
const { createTerraBaseV1Client } = await import('@terrascale/sdk');
const admin = createTerraBaseV1Client({ project, apiKey: required('TERRASCALE_API_KEY'), baseUrl: 'https://api.terrascale.tech' });
const list = async bucketId => {
  const files = [];
  let after;
  do {
    const page = await admin.storage.bucket(bucketId).list(after ? { after } : {});
    files.push(...page.items);
    after = page.next;
  } while (after);
  return files;
};
for (const bucketId of new Set(previous.map(item => item.bucketId))) {
  const files = await list(bucketId);
  const expected = previous.filter(item => item.bucketId === bucketId);
  if (files.length !== expected.length) fail('Previous target inventory differs; reconcile before retirement');
  for (const item of expected) {
    const key = keyOf(item);
    const saved = state.files[key];
    const file = files.find(file => file.id === saved.id);
    if (!file || file.sha256 !== saved.sha256 || file.size !== item.size || file.contentType !== item.contentType || file.metadata.userId !== item.userId || file.metadata.migrationKey !== key) fail('Previous target metadata differs');
  }
}
const journal = { phase: 'retiring', project, retired, nextState };
await writeFile(journalPath, JSON.stringify(journal, null, 2), { flag: 'wx', mode: 0o600 });
for (const item of retired) {
  await admin.storage.bucket(item.bucketId).deleteFile(item.id);
  if ((await list(item.bucketId)).some(file => file.id === item.id)) fail('Retired ID still readable');
}
await writeFile(`${statePath}.tmp`, JSON.stringify(nextState, null, 2), { mode: 0o600 });
await rename(`${statePath}.tmp`, statePath);
journal.phase = 'retired';
await writeFile(`${journalPath}.tmp`, JSON.stringify(journal, null, 2), { mode: 0o600 });
await rename(`${journalPath}.tmp`, journalPath);
console.log(`Retired ${retired.length} obsolete IDs; checkpoint ready for final copy`);
