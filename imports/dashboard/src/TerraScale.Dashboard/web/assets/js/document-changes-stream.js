// Bound incomplete frames as well as individual lines; the feed is an owned read connection.
// UTF-16 characters, not wire bytes: RequestJson.MaxDocumentBytes (1 MiB) may escape 6x.
// SDK 0.0.12 bounds each change cursor at 19,246 characters (id plus JSON cursor).
// 4 KiB covers escaped collection/id/region and identity/envelope keys; see the feature note.
export const maxChangesFrameCharacters = 6 * 1024 * 1024 + 2 * 19246 + 4096;

export class ChangesProtocolError extends Error {}

export async function readChanges(body, signal, receive) {
  const reader = body.getReader();
  const decoder = new TextDecoder('utf-8', { fatal: true });
  let buffer = '', event = '', id, data = [], size = 0;
  try {
    while (!signal.aborted) {
      const chunk = await reader.read();
      if (chunk.done) break;
      buffer += decoder.decode(chunk.value, { stream: true });
      let ending;
      while ((ending = buffer.search(/[\r\n]/)) >= 0) {
        if (buffer[ending] === '\r' && ending === buffer.length - 1) break;
        const line = buffer.slice(0, ending);
        buffer = buffer.slice(ending + (buffer[ending] === '\r' && buffer[ending + 1] === '\n' ? 2 : 1));
        size += line.length;
        if (size > maxChangesFrameCharacters) throw new ChangesProtocolError('Changes frame exceeds the supported size.');
        if (!line) {
          if (data.length) {
            let update;
            try { update = JSON.parse(data.join('\n')); }
            catch { throw new ChangesProtocolError('Invalid changes event JSON.'); }
            if (!['change', 'cursor', 'reconnect', 'resync_required'].includes(event) || update.state !== event ||
                (id !== undefined && id !== update.cursor)) throw new ChangesProtocolError('Invalid changes event.');
            if (await receive(update, id) === false) return;
          }
          event = ''; id = undefined; data = []; size = 0;
        } else if (!line.startsWith(':')) {
          const colon = line.indexOf(':');
          const field = colon < 0 ? line : line.slice(0, colon);
          const value = colon < 0 ? '' : line.slice(colon + 1).replace(/^ /, '');
          if (field === 'event') event = value;
          if (field === 'data') data.push(value);
          if (field === 'id' && !value.includes('\0')) id = value;
        }
      }
      // Complete frames do not count; a final CR is a pending delimiter, not content.
      if (buffer.length - (buffer.endsWith('\r') ? 1 : 0) + size > maxChangesFrameCharacters) throw new ChangesProtocolError('Changes frame exceeds the supported size.');
    }
  } finally {
    await reader.cancel().catch(() => {});
    reader.releaseLock();
  }
}

export async function reconcileDocuments(databaseId, collection, signal, fetcher = fetch) {
  const query = new URLSearchParams({ databaseId, collection });
  const rows = [], cursors = new Set(), ids = new Set();
  while (true) {
    const response = await fetcher(`/api/v1/dashboard/data/documents?${query}`, { signal, credentials: 'same-origin', cache: 'no-store' });
    if (!response.ok) throw new Error(`Current documents could not be reread (HTTP ${response.status}).`);
    const page = await response.json();
    if (!Array.isArray(page.documents) || typeof page.hasMore !== 'boolean' ||
        (page.hasMore ? typeof page.nextCursor !== 'string' || !page.nextCursor : page.nextCursor !== null))
      throw new Error('Invalid current documents response.');
    for (const row of page.documents) {
      if (typeof row.id !== 'string' || ids.has(row.id)) throw new Error('Invalid repeated document identity.');
      ids.add(row.id); rows.push(row);
    }
    if (!page.hasMore) break;
    if (cursors.has(page.nextCursor)) throw new Error('Document pagination did not advance.');
    cursors.add(page.nextCursor); query.set('after', page.nextCursor);
  }
  return rows;
}
