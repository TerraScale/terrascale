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
  const tenants = new Map();
  const branches = [];
  const backups = [];

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

  const sandbox = {
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
    // Mirrors TerraScaleSyncV1Client.subscribe: callers observe a result view,
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

  // Keep the fixture's transport deterministic while presenting the same
  // operation names and result wrappers as @terrascale/sdk 0.3.0.
  sandbox.client = {
    documents: {
      async put(database, collection, id, value) {
        const result = await sandbox.put({ databaseId: database, collection, doc: { ...(value ?? {}), id } });
        return { ok: true, value: { revision: result._rev }, status: 200 };
      },
      async get(database, collection, id) {
        const value = await sandbox.get({ databaseId: database, collection, id });
        return value === null
          ? { ok: false, error: { kind: "not-found", code: "not_found" }, status: 404 }
          : { ok: true, value, status: 200 };
      },
      async patch(database, collection, id, patch) {
        const current = await sandbox.get({ databaseId: database, collection, id });
        if (current === null) return { ok: false, error: { code: "not_found" }, status: 404 };
        const result = await sandbox.put({ databaseId: database, collection, doc: { ...current, ...(patch ?? {}), id } });
        return { ok: true, value: { revision: result._rev }, status: 200 };
      },
      async delete(database, collection, id) {
        const deleted = await sandbox.remove({ databaseId: database, collection, id });
        return deleted ? { ok: true, value: undefined, status: 204 } : { ok: false, error: { code: "not_found" }, status: 404 };
      },
    },
    sql: {
      async execute(database, request) {
        const match = request.sql.match(/FROM\s+["`]?([A-Za-z_][A-Za-z0-9_]*)["`]?\s+WHERE\s+["`]?([A-Za-z_][A-Za-z0-9_]*)["`]?\s*=\s*:(\w+)/iu);
        if (!match) return { ok: false, error: { code: "invalid_sql" }, status: 400 };
        const rows = await sandbox.query({ databaseId: database, collection: match[1], query: { field: match[2], op: "=", value: request.parameters?.[match[3]] } });
        return { ok: true, value: { matched: rows.length, rows: rows.map((value) => ({ id: value.id, value })) }, status: 200 };
      },
    },
    tenants: {
      async create(key) {
        const tenant = { key, databaseId: `stub-tenant-${key}` };
        tenants.set(key, tenant);
        return { ok: true, value: tenant, status: 201 };
      },
      async get(key) {
        const value = tenants.get(key);
        return value ? { ok: true, value, status: 200 } : { ok: false, error: { code: "not_found" }, status: 404 };
      },
      async list() { return { ok: true, value: [...tenants.values()], status: 200 }; },
    },
    databaseBranches: {
      async requestClone(request = {}) {
        const branch = { branchId: `stub-branch-${branches.length + 1}`, sourceDatabaseId: request.sourceDatabaseId ?? databaseId };
        branches.push(branch);
        return { ok: true, value: branch, status: 202 };
      },
    },
    backups: {
      async configurePolicy(database, request) {
        const policy = { databaseId: database, ...request, status: "active" };
        return { ok: true, value: policy, status: 200 };
      },
      async planBackup(database, request) {
        return { ok: true, value: { databaseId: database, ...request, confirmationToken: "stub-confirmation" }, status: 200 };
      },
      async executeBackup(database, request) {
        const backup = { backupId: `stub-backup-${backups.length + 1}`, databaseId: database, ...request, status: "complete" };
        backups.push(backup);
        return { ok: true, value: backup, status: 202 };
      },
      async listBackups(database) { return { ok: true, value: backups.filter((backup) => backup.databaseId === database), status: 200 }; },
    },
  };
  sandbox.sync = {
    subscribe: (input) => sandbox.subscribe(input),
  };
  return sandbox;
}
