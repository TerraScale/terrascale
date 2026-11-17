import { BrowserAuth, createTerraBaseClient } from "/sdk/src/index.js";

let auth;
let db;
let config;
let subscription;
let latest;
let waiters = [];
let reset;

function value(result) {
  if (!result.ok) throw new Error(JSON.stringify(result.error));
  return result.value;
}

function waitFor(predicate) {
  if (latest && predicate(latest)) return Promise.resolve(latest);
  return new Promise((resolve, reject) => { waiters.push({ predicate, resolve, reject }); });
}

export function configure(options) {
  config = options;
  auth = new BrowserAuth(config);
  db = createTerraBaseClient({ project: config.project, baseUrl: config.baseUrl, token: () => auth.accessToken() }).database(config.database);
}

export async function guestProof() {
  const user = value(await auth.signInGuest());
  const notes = db.rows("notes");
  subscription = db.shape("notes", { where: "title <> ''", columns: ["id", "owner_id", "title"] }).subscribe(state => {
    if (!state.upToDate) return;
    latest = [...state.rows.values()];
    const pending = waiters;
    waiters = [];
    for (const waiter of pending) {
      if (waiter.predicate(latest)) waiter.resolve(latest);
      else waiters.push(waiter);
    }
  });
  void subscription.done.catch(error => {
    for (const waiter of waiters) waiter.reject(error);
    waiters = [];
  });
  await waitFor(rows => rows.length === 0);
  const created = value(await notes.create({ title: "SDK guest note" }));
  const observed = await waitFor(rows => rows.some(row => row.title === "SDK guest note"));
  const read = value(await notes.get(created.key, { columns: ["owner_id", "title"] }));
  const updated = value(await notes.update(created.key, { title: "SDK renamed" }));
  const batch = value(await db.batchRows([
    { op: "create", table: "notes", row: { title: "SDK batch note" } },
    { op: "update", table: "notes", key: created.key, row: { title: "SDK batch update" } },
  ]));
  value(await notes.delete(batch.results[0].key));
  const foreign = new BrowserAuth(config);
  const second = value(await foreign.signInGuest());
  const otherDb = createTerraBaseClient({ project: config.project, baseUrl: config.baseUrl, token: () => foreign.accessToken() }).database(config.database);
  const denied = await otherDb.rows("notes").update(created.key, { title: "stolen" });
  value(await foreign.signOut());
  const forbidden = await notes.update(created.key, { owner_id: second.sub });
  const rolledBack = await db.batchRows([
    { op: "update", table: "notes", key: created.key, row: { title: "must roll back" } },
    { op: "update", table: "notes", key: "1", row: { title: "stolen" } },
  ]);
  const retained = value(await notes.get(created.key));
  const rotated = await Promise.all([auth.refresh(), auth.refresh()]);
  const refreshed = value(rotated[0]);
  value(rotated[1]);
  return { user, second, key: created.key, observed, read, updated, denied, forbidden, rolledBack, retained, refreshed, batchSize: batch.results.length };
}

export function watchRulesReset() { reset = waitFor(rows => rows.length === 0); }

export async function routingProof() {
  const remote = createTerraBaseClient({ project: config.project, baseUrl: "https://fra.api.terrascale.tech", token: () => auth.accessToken() }).database(config.database);
  const notes = remote.rows("notes");
  const created = await notes.create({ title: "Routing note" });
  const key = value(created).key;
  const read = await notes.get(key);
  const updated = await notes.update(key, { title: "Routed update" });
  const batch = await remote.batchRows([{ op: "update", table: "notes", key, row: { title: "Routed batch" } }]);
  const removed = await notes.delete(key);
  return [created, read, updated, batch, removed].map(result => { value(result); return result.status; });
}

export async function storageProof() {
  const stored = new Map();
  let blocked = false;
  const storage = { getItem: async key => stored.get(key) ?? null, setItem: async (key, item) => { stored.set(key, item); },
    removeItem: async key => { if (blocked) throw new DOMException("Storage is blocked", "SecurityError"); stored.delete(key); } };
  const options = { ...config, storage, pkceStorage: { ...storage, removeItem: () => { throw new DOMException("Storage is blocked", "SecurityError"); } } };
  const first = new BrowserAuth(options);
  const user = value(await first.signInGuest());
  const restored = new BrowserAuth(options);
  const same = value(await restored.restore()).sub === user.sub;
  blocked = true;
  const signedOut = await first.signOut();
  blocked = false;
  const refused = await restored.refresh();
  return { restored: same, cleared: first.currentUser === null && restored.currentUser === null && stored.size === 0,
    cleanup: signedOut.ok ? null : signedOut.error.code, revoked: refused.ok ? null : refused.error.code };
}
export async function refreshInterleavingProof() {
  const stored = new Map();
  let hold;
  const storage = { getItem: async key => stored.get(key) ?? null, removeItem: async key => { stored.delete(key); },
    setItem: async (key, item) => {
      const gate = hold;
      hold = undefined;
      if (gate) { gate.reached.resolve(); await gate.released.promise; }
      stored.set(key, item);
    } };
  const hostage = () => (hold = { reached: Promise.withResolvers(), released: Promise.withResolvers() });
  const session = new BrowserAuth({ ...config, storage });
  const user = value(await session.signInGuest());
  const before = await session.accessToken();
  let gate = hostage();
  const rotating = session.refresh();
  await gate.reached.promise;
  const joined = session.accessToken();
  const kept = session.currentUser?.sub === user.sub;
  gate.released.resolve();
  value(await rotating);
  const token = await joined;
  const shared = token !== before && token === JSON.parse([...stored.values()][0]).access_token;
  gate = hostage();
  const racing = session.refresh();
  await gate.reached.promise;
  const waiting = session.accessToken().then(() => null, error => error.code);
  const signingOut = session.signOut();
  gate.released.resolve();
  const lost = await racing;
  value(await signingOut);
  return { kept, shared, lost: lost.ok ? null : lost.error.code, waiter: await waiting,
    cleared: session.currentUser === null && stored.size === 0 };
}
export async function rulesReset() { return reset; }

export async function stop() {
  subscription?.close();
  if (subscription) await subscription.done;
  return auth.signOut();
}

export async function beginSignIn() { await auth.signIn(); }
export async function complete() { return auth.completeSignIn(); }
export async function refresh() { return auth.refresh(); }
export function currentUser() { return auth.currentUser; }
export async function signOut() { return auth.signOut(); }
