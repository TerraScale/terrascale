// Stub sandbox branch (D-56 harness, CI mode).
// In-memory stand-in for a real sandbox branch (a cloud dev branch pre-filled
// with the reader's key). The remote client (same interface, fetch-based)
// replaces this once the contract is pinned (P-1) and `make stack` exists
// (E-15); until then every snippet runs here with no credentials.
export function createStubSandbox() {
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
    async put(collection, doc) {
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
    async get(collection, id) {
      return col(collection).get(id) ?? null;
    },
    async remove(collection, id) {
      const existed = col(collection).delete(id);
      if (existed) notify(collection, { collection, removedId: id });
      return existed;
    },
    // Minimal SQL-ish surface for snippets: filter over extracted fields.
    // Real SQL runs over POST /api/v1/databases/{id}/sql (D-54/D-78) once P-1 lands.
    async query(collection, predicate) {
      return [...col(collection).values()].filter(predicate);
    },
    // Minimal live-query surface: callback per committed row in collection.
    // Real subscribe uses CDN-backed HTTP long-polling on sync.api. (D-14).
    subscribe(collection, cb) {
      if (!watchers.has(collection)) watchers.set(collection, []);
      watchers.get(collection).push(cb);
      return () => {
        watchers.set(
          collection,
          (watchers.get(collection) ?? []).filter((f) => f !== cb),
        );
      };
    },
    async createTenant(key) {
      if (!key || typeof key !== "string") throw new Error("tenant key required");
      return { databaseId: `db_tenant_${key}`, key };
    },
  };
}
