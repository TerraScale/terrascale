import { ChangesProtocolError, readChanges, reconcileDocuments } from './document-changes-stream.js';

export class DocumentChanges extends HTMLElement {
  connectedCallback() {
    if (this.connection) return;
    const panel = this.querySelector('#document-changes');
    if (!panel) return;
    this.connection = new AbortController();
    this.run(panel, this.connection.signal);
  }

  disconnectedCallback() {
    this.connection?.abort();
    this.connection = undefined;
  }

  async run(panel, signal) {
    const { databaseId, collection } = panel.dataset;
    const status = panel.querySelector('#document-changes-status');
    const items = panel.querySelector('#document-changes-items');
    let cursor, resync = false, historyLost = false;
    const seen = new Set();
    const show = text => { status.textContent = text; };
    const append = text => {
      const row = document.createElement('li');
      row.textContent = text;
      items.append(row);
      while (items.children.length > 100) items.firstElementChild.remove();
    };
    while (!signal.aborted) {
      let stop = false;
      let reconnectNow = false;
      try {
        show(historyLost ? 'Reconnecting; retained changes unavailable.' : 'Connecting to document changes…');
        const query = new URLSearchParams({ collection });
        if (resync) query.set('cursor', 'now');
        const headers = { Accept: 'text/event-stream' };
        if (cursor && !resync) headers['Last-Event-ID'] = cursor;
        const response = await fetch(`/v1/dashboard/databases/${encodeURIComponent(databaseId)}/changes?${query}`,
          { headers, credentials: 'same-origin', cache: 'no-store', signal });
        if (!response.ok) {
          await response.body?.cancel();
          stop = ![408, 429, 500, 502, 503, 504].includes(response.status);
          throw new Error(`Changes request failed (HTTP ${response.status}).${response.status === 400 ? ' Invalid cursor; no changes were accepted.' : ''}`);
        }
        if (!response.headers.get('content-type')?.startsWith('text/event-stream') || !response.body) {
          stop = true;
          throw new Error('Changes response was not an event stream.');
        }
        await readChanges(response.body, signal, async (update, id) => {
          if (update.state === 'resync_required') {
            cursor = undefined; resync = true; historyLost = true;
            seen.clear(); reconnectNow = true;
            show('Retained changes unavailable. Establishing a new checkpoint before rereading documents…');
            return false;
          }
          const candidate = id ?? cursor;
          if (update.state === 'change') {
            const change = update.change;
            if (!change || change.collection !== collection || !['upsert', 'delete'].includes(change.op) ||
                ['origin', 'version', 'id'].some(field => typeof change[field] !== 'string' || !change[field]))
              throw new ChangesProtocolError('Changes returned an invalid change or a different collection.');
            const identity = JSON.stringify([change.origin, change.version, change.collection, change.id]);
            if (!seen.has(identity)) {
              append(`${change.op} · ${change.id} · ${change.version}`);
              seen.add(identity);
              if (seen.size > 1024) seen.delete(seen.values().next().value);
            }
          }
          if (resync && candidate && (update.state === 'cursor' || update.state === 'change')) {
            // Apply the event before accepting its checkpoint; establish the stream before rereading documents.
            const rows = await reconcileDocuments(databaseId, collection, signal);
            for (const row of rows.slice(0, 100)) append(`Current document ${row.id}: ${JSON.stringify(row.value)}`);
            resync = false;
          }
          cursor = candidate;
          if (update.state !== 'reconnect') show(resync
            ? 'Retained changes unavailable. Establishing a new checkpoint before rereading documents…'
            : historyLost ? 'Resynced; retained changes unavailable' : 'Connected to document changes.');
          return update.state !== 'reconnect';
        });
      } catch (error) {
        if (signal.aborted) return;
        if (error instanceof ChangesProtocolError) stop = true;
        show(error.message);
      }
      if (stop || signal.aborted) return;
      if (reconnectNow) continue;
      await new Promise(resolve => {
        const done = () => { clearTimeout(timer); signal.removeEventListener('abort', done); resolve(); };
        const timer = setTimeout(done, 2000);
        signal.addEventListener('abort', done, { once: true });
      });
    }
  }
}

if (!customElements.get('ts-document-changes')) customElements.define('ts-document-changes', DocumentChanges);
