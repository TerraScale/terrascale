import { readFile, writeFile, rename, realpath, lstat } from 'node:fs/promises';
import { resolve, sep } from 'node:path';
import { createHash } from 'node:crypto';

const [manifestPath, rootPath, statePath, mode] = process.argv.slice(2);
process.on('uncaughtException', () => {
  console.error('Copy stopped; inspect checkpoint phase and reconcile without logging credentials or signed URLs');
  process.exitCode = 1;
});
const fail = message => { console.error(message); throw new Error('Copy stopped'); };
const hash = bytes => createHash('sha256').update(bytes).digest('base64');
if (!manifestPath || !rootPath || !statePath || !['--dry-run', '--apply'].includes(mode)) {
  fail('Usage: node copy.mjs manifest.json staging state.json --dry-run|--apply');
}
const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
if (!Array.isArray(manifest)) fail('Manifest must be an array');
const root = await realpath(rootPath);
const items = [];
const keys = new Set();
for (const item of manifest) {
  if (!item.bucket || !item.name || !item.bucketId || !item.contentType || !item.userId) fail('Missing manifest field');
  if (typeof item.userId !== 'string' || !/^[0-7][0-9A-HJKMNP-TV-Z]{25}$/.test(item.userId)) fail('Target owner must be a canonical ULID');
  const key = JSON.stringify([item.bucket, item.name]);
  if (keys.has(key)) fail('Duplicate source object');
  keys.add(key);
  const path = resolve(root, item.bucket, item.name);
  if (!path.startsWith(root + sep) || (await lstat(path)).isSymbolicLink()) fail('Unsafe local path');
  const canonical = await realpath(path);
  if (!canonical.startsWith(root + sep)) fail('Local path leaves staging');
  const bytes = await readFile(canonical);
  if (bytes.length !== item.size || bytes.length < 1 || bytes.length > 104857600) fail('Source size mismatch or unsupported size');
  const sha256 = hash(bytes);
  const metadata = { userId: item.userId, migrationKey: key };
  if (Buffer.byteLength(JSON.stringify(metadata)) > 16384) fail('Metadata exceeds 16 KiB');
  items.push({ ...item, key, path: canonical, sha256, metadata });
}
const total = items.reduce((sum, item) => sum + item.size, 0);
console.log(`Validated ${items.length} objects, ${total} bytes`);
if (mode === '--dry-run') {
  console.log('Dry run: no network requests or state writes');
  process.exit(0);
}
const required = name => process.env[name] || fail(`Missing ${name}`);
const project = required('TERRASCALE_PROJECT_ID');
const { createTerraBaseV1Client } = await import('@terrascale/sdk');
const client = createTerraBaseV1Client({
  project,
  apiKey: required('TERRASCALE_API_KEY'),
  baseUrl: 'https://api.terrascale.tech',
});
let state;
try { state = JSON.parse(await readFile(statePath, 'utf8')); }
catch (error) {
  if (error.code !== 'ENOENT') throw error;
  state = { project, files: {} };
}
if (state.project !== project) fail('State belongs to another project');
const save = async () => {
  await writeFile(`${statePath}.tmp`, JSON.stringify(state, null, 2), { mode: 0o600 });
  await rename(`${statePath}.tmp`, statePath);
};
for (const item of items) {
  const bucket = client.storage.bucket(item.bucketId);
  let saved = state.files[item.key];
  if (saved && (saved.sha256 !== item.sha256 || saved.bucketId !== item.bucketId)) fail('Source or bucket changed; reconcile state');
  if (saved && saved.phase !== 'verified') fail('Uncertain upload: reconcile pending state before resuming');
  if (!saved) {
    saved = { phase: 'allocating', bucketId: item.bucketId, sha256: item.sha256, size: item.size };
    state.files[item.key] = saved;
    await save();
    const capability = await bucket.createUpload({
      name: item.name, size: item.size, contentType: item.contentType,
      sha256: item.sha256, metadata: item.metadata,
    });
    saved.id = capability.id;
    saved.phase = 'pending';
    await save();
    const url = new URL(capability.url);
    if (url.protocol !== 'https:' || url.username || url.password) fail('Invalid upload capability');
    const headers = new Headers(capability.headers);
    for (const name of headers.keys()) {
      if (!['content-type', 'content-length', 'x-amz-checksum-sha256'].includes(name)) fail('Unsafe capability header');
    }
    const bytes = await readFile(item.path);
    if (hash(bytes) !== item.sha256) fail('Source changed after inventory');
    headers.set('Content-Length', String(bytes.length));
    const body = new ReadableStream({ start(controller) { controller.enqueue(bytes); controller.close(); } });
    const response = await fetch(url, { method: 'PUT', headers, body, duplex: 'half', credentials: 'omit', redirect: 'error' });
    await response.body?.cancel();
    if (!response.ok) fail(`Upload HTTP ${response.status}; reconcile pending state`);
    await bucket.completeUpload(saved.id);
  }
  const downloaded = Buffer.from(await (await bucket.download(saved.id)).arrayBuffer());
  if (downloaded.length !== item.size || hash(downloaded) !== item.sha256) fail('Downloaded bytes differ');
  saved.phase = 'verified';
  await save();
}
const expected = new Map();
for (const item of items) {
  if (!expected.has(item.bucketId)) expected.set(item.bucketId, []);
  expected.get(item.bucketId).push(item);
}
for (const [bucketId, group] of expected) {
  const actual = [];
  let after;
  do {
    const page = await client.storage.bucket(bucketId).list(after ? { after } : {});
    actual.push(...page.items);
    after = page.next;
  } while (after);
  if (actual.length !== group.length || actual.reduce((sum, file) => sum + file.size, 0) !== group.reduce((sum, item) => sum + item.size, 0)) fail('Target count or size differs; use a dedicated migration bucket');
  for (const item of group) {
    const file = actual.find(file => file.id === state.files[item.key].id);
    if (!file || file.size !== item.size || file.sha256 !== item.sha256 || file.contentType !== item.contentType || file.metadata.userId !== item.userId || file.metadata.migrationKey !== item.key) fail('Target metadata differs');
  }
}
console.log(`Verified ${items.length} objects, ${total} bytes; downloads, checksums and target inventory match`);
