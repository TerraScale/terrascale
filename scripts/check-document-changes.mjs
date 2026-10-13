import assert from 'node:assert/strict';
import { readChanges, reconcileDocuments } from '../src/TerraScale.Dashboard/web/assets/js/document-changes-stream.js';
import { copySavedQuery } from '../src/TerraScale.Dashboard/web/assets/js/code-editor/saved-query.js';

const encoder = new TextEncoder();
const signal = new AbortController().signal;
let disposed = false;
function body(text, incomplete = false) {
  const bytes = encoder.encode(text);
  return new ReadableStream({
    start(controller) {
      // Byte boundaries deliberately split UTF-8 and CRLF sequences.
      const width = bytes.length > 1000 ? 16384 : 1;
      for (let offset = 0; offset < bytes.length; offset += width) controller.enqueue(bytes.slice(offset, offset + width));
      if (!incomplete) controller.close();
    },
    cancel() { disposed = true; }
  });
}
const seen = [];
await readChanges(body(': heartbeat\r\nevent: change\r\nid: opaque+/=\r\ndata: {"state":"change","cursor":"opaque+/=",\r\ndata: "change":{"id":"é"}}\r\n\r\nevent: cursor\nid: next\ndata: {"state":"cursor","cursor":"next"}\n\n'), signal,
  (update, id) => { seen.push([update, id]); });
assert.equal(seen.length, 2);
assert.equal(seen[0][0].change.id, 'é');
assert.equal(seen[0][1], 'opaque+/=');
assert.equal(seen[1][1], 'next');
await assert.rejects(() => readChanges(body('event: cursor\nid: wrong\ndata: {"state":"cursor","cursor":"other"}\n\n'), signal, () => {}), /Invalid changes event/);
await assert.rejects(() => readChanges(body('x'.repeat(1024 * 1024 + 1)), signal, () => {}), /exceeds/);
await readChanges(body('event: cursor\ndata: {"state":"cursor","cursor":"unfinished"}', false), signal, () => { assert.fail('Incomplete SSE event must not be accepted.'); });
await readChanges(body('event: resync_required\ndata: {"state":"resync_required","cursor":null}\n\n', true), signal, () => false);
assert.equal(disposed, true);
let requested;
assert.deepEqual(await reconcileDocuments('db /', 'one+collection', signal, async (url, options) => {
  requested = url;
  assert.equal(options.signal, signal);
  assert.equal(options.credentials, 'same-origin');
  return Response.json([{ id: 'owned', value: { text: '<script>' } }]);
}), [{ id: 'owned', value: { text: '<script>' } }]);
const query = new URL(requested, 'https://dashboard.test').searchParams;
assert.equal(query.get('databaseId'), 'db /');
assert.equal(query.get('collection'), 'one+collection');
await assert.rejects(() => reconcileDocuments('db', 'same', signal, async () => new Response('', { status: 400 })), /HTTP 400/);
console.log('Document changes reader: UTF-8/chunk framing, checkpoint validation, bounds, disposal and document reread passed.');
const source = { value: 'SELECT latest FROM items' };
let mirrored = 0;
const target = { value: 'old draft', dispatchEvent(event) { assert.equal(event.type, 'input'); mirrored++; } };
const dialog = { dataset: {} };
copySavedQuery(source, target, dialog);
assert.equal(target.value, source.value);
target.value = 'modal edit';
copySavedQuery(source, target, dialog);
assert.equal(target.value, 'modal edit');
source.value = 'new editor input';
copySavedQuery(source, target, dialog);
assert.equal(target.value, source.value);
assert.equal(mirrored, 2);
console.log('Saved query draft copies current SQL on activation and preserves subsequent modal edits.');
