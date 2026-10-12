import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { request as httpRequest } from 'node:http';
import { createTerraBaseV1Client, generateId } from '@terrascale/sdk';

// TUnit supplies actual native signup/database/key results through stdin. The
// published SDK receives them unchanged; credentials never enter process args.
const input = JSON.parse(readFileSync(0, 'utf8'));
const metadata = JSON.parse(readFileSync(fileURLToPath(new URL('../package.json', import.meta.resolve('@terrascale/sdk'))), 'utf8'));
assert.equal(metadata.version, '0.0.3', 'Use the exact public npm SDK pinned by the checkout');
assert.equal(Number(process.versions.node.split('.')[0]), 24, 'Run customer SDK qualification on its supported Node 24 runtime');
assert.match(input.projectId, /^[0-7][0-9A-HJKMNP-TV-Z]{25}$/u,
  'Use the canonical ULID project returned by the current producer, without translating it');
let requests = 0;
const client = createTerraBaseV1Client({
  project: input.projectId,
  token: input.secret,
  baseUrl: 'https://gru.api.terrascale.tech',
  fetch: async (url, options) => {
    const original = new URL(url);
    assert.equal(original.origin, 'https://gru.api.terrascale.tech');
    requests++;
    // Only the physical destination changes for the local HTTP producer.
    // Project/document IDs, routes, headers and bodies remain SDK-owned.
    const headers = new Headers(options?.headers);
    headers.set('Host', original.host);
    // Node24's native fetch replaces Host with the physical URL host. This
    // fetch-compatible TCP boundary preserves the SDK's actual wire identity.
    return new Promise((resolve, reject) => {
      const request = httpRequest(input.providerOrigin, {
        method: options.method,
        path: original.pathname + original.search,
        headers: Object.fromEntries(headers),
        signal: options.signal
      }, response => {
        const chunks = [];
        response.on('data', chunk => chunks.push(chunk));
        response.on('error', reject);
        response.on('end', () => {
          const responseHeaders = new Headers();
          for (let index = 0; index < response.rawHeaders.length; index += 2)
            responseHeaders.append(response.rawHeaders[index], response.rawHeaders[index + 1]);
          resolve(new Response(Buffer.concat(chunks), {
            status: response.statusCode,
            statusText: response.statusMessage,
            headers: responseHeaders
          }));
        });
      });
      request.on('error', reject);
      request.end(options.body);
    });
  }
});
const collection = client.database(input.databaseName).collection('users');
const id = generateId();
const created = await collection.put(id, { title: 'Customer SDK flow', done: false }, { ifNoneMatchAny: true });
assert.equal(created.id, id);
assert.equal(created.created, true);
assert.match(created.offset, /^(?:0|[1-9][0-9]*)$/u);
const document = await collection.get(id);
assert.equal(document.version, created.version);
assert.deepEqual(document.document, { title: 'Customer SDK flow', done: false });
const patched = await collection.patch(id, { merge: { done: true } }, { ifMatch: document.version });
const updated = await collection.get(id);
assert.equal(updated.version, patched.version);
assert.equal(updated.document.done, true);
assert.equal(updated.document.title, 'Customer SDK flow');
await assert.rejects(collection.put(id, { title: 'stale' }, { ifMatch: document.version }),
  error => error.status === 412 && error.code === 'precondition_failed');
const replaced = await collection.put(id, { title: 'Customer SDK replaced', done: true }, { ifMatch: updated.version });
assert.equal(replaced.created, false);
const deleted = await collection.delete(id, { ifMatch: replaced.version });
assert.equal(deleted.id, id);
await assert.rejects(collection.get(id), error => error.status === 404 && error.code === 'not_found');
assert.equal(requests, 8, 'Customer CRUD and stale-write/absent-read checks traverse the unchanged SDK');
process.stdout.write(JSON.stringify({ sdk: metadata.version, node: process.versions.node, projectId: input.projectId,
  documentId: id, completed: true, requests, offset: deleted.offset }) + '\n');
