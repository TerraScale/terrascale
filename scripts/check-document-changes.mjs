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
  return Response.json({ documents: [{ id: 'owned', value: { text: '<script>' } }], nextCursor: null, hasMore: false });
}), [{ id: 'owned', value: { text: '<script>' } }]);
const query = new URL(requested, 'https://dashboard.test').searchParams;
assert.equal(query.get('databaseId'), 'db /');
assert.equal(query.get('collection'), 'one+collection');
await assert.rejects(() => reconcileDocuments('db', 'same', signal, async () => new Response('', { status: 400 })), /HTTP 400/);
let pages = 0;
assert.deepEqual(await reconcileDocuments('db', 'same', signal, async url => {
  const after = new URL(url, 'https://dashboard.test').searchParams.get('after');
  pages++;
  if (!after) return Response.json({ documents: [], nextCursor: 'opaque+/=', hasMore: true });
  assert.equal(after, 'opaque+/=');
  return Response.json({ documents: [{ id: 'second', value: {} }], nextCursor: null, hasMore: false });
}), [{ id: 'second', value: {} }]);
assert.equal(pages, 2);
await assert.rejects(() => reconcileDocuments('db', 'same', signal, async () => Response.json({ documents: [], nextCursor: 'repeat', hasMore: true })), /did not advance/);
await assert.rejects(() => reconcileDocuments('db', 'same', signal, async () => Response.json({ documents: [], nextCursor: null, hasMore: true })), /Invalid current documents/);
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

// Exercise the browser receiver with deterministic DOM/network boundaries, without reconnect sleeps.
globalThis.HTMLElement = class {};
globalThis.customElements = { get() { return true; } };
const { DocumentChanges } = await import('../src/TerraScale.Dashboard/web/assets/js/document-changes.js');
const originalFetch = globalThis.fetch;
const originalDocument = globalThis.document;
const originalTimeout = globalThis.setTimeout;
globalThis.setTimeout = callback => { queueMicrotask(callback); return 0; };
globalThis.document = { createElement() { return { textContent: '' }; } };
function event(state, cursor, change) {
  return `event: ${state}\n${cursor == null ? '' : `id: ${cursor}\n`}data: ${JSON.stringify({ state, cursor, change })}\n\n`;
}
function panel(append) {
  const statuses = [];
  const status = { set textContent(value) { statuses.push(value); } };
  const items = { children: [], append(row) { append?.(row); this.children.push(row); } };
  return { statuses, items, dataset: { databaseId: 'db', collection: 'items' },
    querySelector(id) { return id === '#document-changes-status' ? status : items; } };
}
const change = id => ({ collection: 'items', id, op: 'upsert', origin: 'origin', version: 'version', document: null });
const stream = text => new Response(body(text), { headers: { 'content-type': 'text/event-stream' } });
try {
  let calls = 0, applications = 0;
  const controller = new AbortController();
  const failedApply = panel(() => { if (++applications === 1) throw new Error('Synthetic apply failure'); });
  globalThis.fetch = async (url, options) => {
    assert.match(url, /\/v1\/dashboard\/databases\/db\/changes/);
    calls++;
    if (calls === 1) return stream(event('cursor', 'prior') + event('change', 'next', change('retry')));
    if (calls === 2) {
      assert.equal(options.headers['Last-Event-ID'], 'prior', 'Failed application must retain the prior checkpoint.');
      return stream(event('change', 'next', change('retry')));
    }
    assert.equal(options.headers['Last-Event-ID'], 'next');
    controller.abort();
    return stream('');
  };
  await new DocumentChanges().run(failedApply, controller.signal);
  assert.equal(applications, 2, 'A failed append must not enter the dedup set.');
  assert.equal(failedApply.items.children.length, 1);

  calls = 0;
  let snapshots = 0;
  const resyncController = new AbortController();
  const resyncPanel = panel();
  globalThis.fetch = async (url, options) => {
    if (url.startsWith('/api/v1/dashboard/data/documents')) {
      snapshots++;
      assert.equal(resyncPanel.items.children.length, 2, 'Apply both transaction changes before accepting its final checkpoint.');
      assert.match(resyncPanel.statuses.at(-1), /Establishing a new checkpoint/);
      assert.equal(resyncPanel.statuses.some(value => value.startsWith('Resynced')), false);
      return Response.json({ documents: [{ id: 'current', value: {} }], nextCursor: null, hasMore: false });
    }
    calls++;
    if (calls === 1) return stream(event('resync_required', null));
    if (calls === 2) {
      assert.equal(new URL(url, 'https://dashboard.test').searchParams.get('cursor'), 'now');
      assert.equal(options.headers['Last-Event-ID'], undefined);
      return stream(event('change', null, change('first')) + event('change', 'final', change('last')));
    }
    assert.equal(options.headers['Last-Event-ID'], 'final');
    resyncController.abort();
    return stream('');
  };
  await new DocumentChanges().run(resyncPanel, resyncController.signal);
  assert.equal(snapshots, 1, 'The first checkpoint on a change must trigger the document snapshot.');
  assert.equal(resyncPanel.statuses.includes('Resynced; retained changes unavailable'), true);
} finally {
  globalThis.fetch = originalFetch;
  globalThis.document = originalDocument;
  globalThis.setTimeout = originalTimeout;
}
console.log('Browser feed: failed application retains its checkpoint; checkpointless resync waits for an applied final change.');
