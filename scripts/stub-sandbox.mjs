// Stub sandbox branch (D-56 harness, CI mode).
// In-memory stand-in for a real sandbox branch (a cloud dev branch pre-filled
// with the reader's key). The remote client (same interface, fetch-based)
// replaces this once the contract is pinned (P-1) and `make stack` exists
// (E-15); until then every snippet runs here with no credentials.
export function createStubSandbox() {
  const databaseId = "stub-database";
  /** @type {Map<string, Map<string, any>>} */
  const collections = new Map();
  /** @type {Array<{collection: string, doc: any}>} */
  const journal = [];
  /** @type {Map<string, Array<(delta: any) => void>>} */
  const watchers = new Map();

  function col(name) {
    let c = collections.get(name);
    if (!c) {
      c = new Map();
      collections.set(name, c);
    }
    return c;
  }

  function notify(collection, delta) {
    for (const cb of watchers.get(collection) ?? []) cb(delta);
  }

  return {
    mode: "stub",
    databaseId,
    async put({ databaseId: requestedDatabaseId, collection, doc }) {
      if (requestedDatabaseId !== databaseId) throw new Error("stub databaseId does not match");
      if (!doc || typeof doc.id !== "string" || !doc.id) {
        throw new Error("doc must have a string id");
      }
      const stored = { ...doc, _rev: `rev_${journal.length + 1}` };
      col(collection).set(doc.id, stored);
      const delta = { collection, doc: stored };
      journal.push(delta);
      notify(collection, delta);
      return stored;
    },
    async get({ databaseId: requestedDatabaseId, collection, id }) {
      if (requestedDatabaseId !== databaseId) throw new Error("stub databaseId does not match");
      return col(collection).get(id) ?? null;
    },
    async remove({ databaseId: requestedDatabaseId, collection, id }) {
      if (requestedDatabaseId !== databaseId) throw new Error("stub databaseId does not match");
      const existed = col(collection).delete(id);
      if (existed) notify(collection, { collection, removedId: id });
      return existed;
    },
    // Minimal SQL-ish surface for snippets: filter over extracted fields.
    // Real SQL runs over POST /api/v1/databases/{id}/sql (D-54/D-78) once P-1 lands.
    async query({ databaseId: requestedDatabaseId, collection, query }) {
      if (requestedDatabaseId !== databaseId) throw new Error("stub databaseId does not match");
      return [...col(collection).values()].filter((doc) => doc[query.field] === query.value);
    },
    // Mirrors TerraScaleSyncV2Client.subscribe: callers observe a result view,
    // not a callback carrying an invented JSON delta shape.
    async subscribe({ databaseId: requestedDatabaseId, subscription, query }) {
      if (requestedDatabaseId !== databaseId) throw new Error("stub databaseId does not match");
      const collection = query.match(/FROM\s+([A-Za-z_][A-Za-z0-9_]*)/iu)?.[1];
      if (!collection) throw new Error("stub query must name a collection");
      const listeners = new Set();
      const view = {
        subscribe(listener) {
          listeners.add(listener);
          return () => listeners.delete(listener);
        },
        getVisibleItems() {
          return [...(collections.get(collection)?.values() ?? [])].map((doc) => ({ identity: doc.id }));
        },
      };
      const notifyView = () => { for (const listener of listeners) listener(); };
      const watcher = (delta) => { if (delta.collection === collection) notifyView(); };
      if (!watchers.has(collection)) watchers.set(collection, []);
      watchers.get(collection).push(watcher);
      let closed = false;
      return {
        id: subscription,
        view,
        close() {
          if (closed) return;
          closed = true;
          watchers.set(collection, (watchers.get(collection) ?? []).filter((f) => f !== watcher));
        },
      };
    },
  };
}
