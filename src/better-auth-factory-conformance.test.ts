import { strict as assert } from "node:assert";
import { describe, test } from "vitest";
import { createTerraScaleAuthAdapter } from "./better-auth-factory.js";
import { AuthPersistenceClient } from "./auth-persistence.js";

type Seen = { readonly request: Request; readonly body: unknown };

function client(seen: Seen[], response?: (request: Request, body: unknown) => Response | Promise<Response>) {
  return new AuthPersistenceClient({
    apiBaseUrl: "https://api.example.test",
    fetch: async (input, init) => {
      const request = new Request(input, init);
      const body = request.body === null ? undefined : JSON.parse(await request.text()) as unknown;
      seen.push({ request, body });
      if (response !== undefined) return await response(request, body);
      const pathname = new URL(request.url).pathname;
      if (pathname.endsWith("/transactions/begin")) return Response.json({ matched: true, record: null, transactionId: "tx#1", expiresAtMs: Date.now() + 30_000 });
      if (pathname.endsWith("/transactions/commit") || pathname.endsWith("/transactions/rollback")) return Response.json({ matched: true, record: null });
      if (pathname.endsWith("/index")) return Response.json({ matched: true, record: null });
      return Response.json({ matched: true, record: null, records: [] });
    },
  });
}

const options = { appName: "conformance", secret: "test-secret-test-secret-test-secret" };

describe("Better Auth 1.7.2 factory conformance", () => {
  test("uses the configured ID generator and sends Date values as ISO wire strings", async () => {
    const seen: Seen[] = [];
    const adapter = createTerraScaleAuthAdapter(client(seen, (request, body) => {
      if (new URL(request.url).pathname.endsWith("/index")) return Response.json({ matched: true, record: null });
      const record = body && typeof body === "object" ? { ...((body as Record<string, unknown>).record as Record<string, unknown>), createdAt: "2026-01-02T03:04:05.000Z" } : null;
      return Response.json({ matched: true, record });
    }), "auth")({
      ...options,
      advanced: { database: { generateId: () => "generated-user-id" } },
    });
    const created = await adapter.create({ model: "user", data: { name: "Mario", email: "mario@example.test", createdAt: new Date("2026-01-02T03:04:05Z") } });
    const request = seen.find((entry) => new URL(entry.request.url).pathname.endsWith("/insertUnique"));
    assert.ok(request);
    const record = (request.body as { record: Record<string, unknown> }).record;
    assert.equal(record.id, "generated-user-id");
    assert.equal(record.createdAt, "2026-01-02T03:04:05.000Z");
    assert.ok(created.createdAt instanceof Date);
  });

  test("maps custom model and field names through predicates and sorting", async () => {
    const seen: Seen[] = [];
    const adapter = createTerraScaleAuthAdapter(client(seen), "auth")({
      ...options,
      user: { modelName: "members", fields: { email: "email_address", name: "display_name" } },
    });
    await adapter.findMany({ model: "user", where: [{ field: "email", value: "mario@example.test" }], limit: 5, sortBy: { field: "email", direction: "asc" } });
    const request = seen.find((entry) => new URL(entry.request.url).pathname.endsWith("/findPage"));
    assert.ok(request);
    const body = request.body as { model: string; predicates: readonly { field: string }[]; sortBy: { field: string } };
    assert.equal(body.model, "members");
    assert.equal(body.predicates[0]?.field, "email_address");
    assert.equal(body.sortBy.field, "email_address");
  });

  test("omits an empty select list so an empty select means all fields", async () => {
    const seen: Seen[] = [];
    const adapter = createTerraScaleAuthAdapter(client(seen), "auth")(options);
    await adapter.findOne({ model: "user", where: [], select: [] });
    const request = seen.find((entry) => new URL(entry.request.url).pathname.endsWith("/findPage"));
    assert.ok(request);
    assert.equal("select" in (request.body as Record<string, unknown>), false);
  });

  test("normalizes native joins to one transactional findPage request", async () => {
    const seen: Seen[] = [];
    const adapter = createTerraScaleAuthAdapter(client(seen), "auth")(options);
    await adapter.transaction(async (transaction) => transaction.findOne({ model: "user", where: [{ field: "id", value: "u1" }], join: { account: { limit: 2 } } }));
    const pages = seen.filter((entry) => new URL(entry.request.url).pathname.endsWith("/findPage"));
    assert.equal(pages.length, 1);
    const body = pages[0]?.body as { joins: readonly Record<string, unknown>[] };
    assert.deepEqual(body.joins, [{ model: "account", outputField: "account", fromField: "id", toField: "userId", relation: "one-to-many", limit: 2 }]);
  });

  test("rolls back when the serialized transaction callback fails", async () => {
    const seen: Seen[] = [];
    const adapter = createTerraScaleAuthAdapter(client(seen), "auth")(options);
    await assert.rejects(adapter.transaction(async () => { throw new Error("callback failed"); }), /callback failed/u);
    const paths = seen.map(({ request }) => new URL(request.url).pathname);
    assert.equal(paths.some((path) => path.endsWith("/transactions/begin")), true);
    assert.equal(paths.some((path) => path.endsWith("/transactions/rollback")), true);
    assert.equal(paths.some((path) => path.endsWith("/transactions/commit")), false);
  });

  test("preserves callback invocation order when concurrent operations complete out of order", async () => {
    const seen: Seen[] = [];
    const adapter = createTerraScaleAuthAdapter(client(seen, asyncResponse), "auth")(options);
    await adapter.transaction(async (transaction) => {
      await Promise.all([
        transaction.findMany({ model: "user", limit: 1 }),
        transaction.findMany({ model: "session", limit: 1 }),
      ]);
    });
    const pages = seen.filter(({ request }) => new URL(request.url).pathname.endsWith("/findPage"));
    assert.equal((pages[0]?.body as { model: string }).model, "user");
    assert.equal((pages[1]?.body as { model: string }).model, "session");
  });

  test("does not begin a transaction when unique-index provisioning fails", async () => {
    const seen: Seen[] = [];
    const adapter = createTerraScaleAuthAdapter(client(seen, (request) => new Response(JSON.stringify({ error: { code: "forbidden", message: "schema index denied" } }), { status: request.url.endsWith("/index") ? 403 : 200, headers: { "content-type": "application/json" } })), "auth")({
      ...options,
      user: { fields: { email: "email_address" } },
    });
    await assert.rejects(adapter.transaction(async (transaction) => transaction.create({ model: "user", data: { name: "Mario", email: "mario@example.test" } })), /schema index denied/u);
    assert.equal(seen.some((entry) => new URL(entry.request.url).pathname.endsWith("/transactions/begin")), false);
  });

  test("rejects malformed record results instead of returning them", async () => {
    const adapter = createTerraScaleAuthAdapter(client([], (request) => request.url.endsWith("/findPage") ? Response.json({ matched: true, records: [null] }) : Response.json({ matched: true, record: null })), "auth")(options);
    await assert.rejects(adapter.findMany({ model: "user", limit: 1 }), /invalid record/u);
  });
});

async function asyncResponse(request: Request, body: unknown): Promise<Response> {
  const model = body && typeof body === "object" && "model" in body ? (body as { model: string }).model : "";
  if (new URL(request.url).pathname.endsWith("/findPage")) {
    await new Promise((resolve) => setTimeout(resolve, model === "user" ? 20 : 0));
    return Response.json({ matched: true, records: [] });
  }
  const pathname = new URL(request.url).pathname;
  if (pathname.endsWith("/index")) return Response.json({ matched: true, record: null });
  if (pathname.endsWith("/transactions/begin")) return Response.json({ matched: true, record: null, transactionId: "tx#2", expiresAtMs: Date.now() + 30_000 });
  return Response.json({ matched: true, record: null });
}


test("serializes asynchronous official input transforms before sending operations", async () => {
  const seen: Seen[] = [];
  let releaseFirst!: () => void;
  let signalFirst!: () => void;
  const firstStarted = new Promise<void>(resolve => { signalFirst = resolve; });
  const release = new Promise<void>(resolve => { releaseFirst = resolve; });
  const transformed: string[] = [];
  const adapter = createTerraScaleAuthAdapter(client(seen, echoMutations), "auth")({
    ...options,
    user: { additionalFields: { probe: { type: "string", transform: { input: async (value) => {
      transformed.push(String(value));
      if (value === "first") { signalFirst(); await release; }
      return value;
    } } } } },
  });
  const pending = adapter.transaction(async transaction => {
    await Promise.all([
      transaction.create({ model: "user", data: { email: "first@example.test", probe: "first" } }),
      transaction.create({ model: "user", data: { email: "second@example.test", probe: "second" } }),
    ]);
  });
  await firstStarted;
  assert.deepEqual(transformed, ["first"]);
  releaseFirst();
  await pending;
  assert.deepEqual(transformed, ["first", "second"]);
  const writes = seen.filter(({request}) => request.url.endsWith("/insertUnique"));
  assert.deepEqual(writes.map(({body}) => (body as {record:{probe:string}}).record.probe), ["first", "second"]);
});

test("a caught official input transform failure rolls back earlier staged writes", async () => {
  const seen: Seen[] = [];
  const adapter = createTerraScaleAuthAdapter(client(seen, echoMutations), "auth")({
    ...options,
    user: { additionalFields: { probe: { type: "string", transform: { input: async (value) => {
      if (value === "reject") throw new Error("input transform rejected");
      return value;
    } } } } },
  });
  await assert.rejects(adapter.transaction(async transaction => {
    await transaction.create({ model: "user", data: { email: "ok@example.test", probe: "ok" } });
    try {
      await transaction.create({ model: "user", data: { email: "bad@example.test", probe: "reject" } });
    } catch { /* A caught operation failure must still poison the transaction. */ }
  }), /input transform rejected/u);
  assert.equal(seen.filter(({request}) => request.url.endsWith("/insertUnique")).length, 1);
  assert.equal(seen.some(({request}) => request.url.endsWith("/transactions/rollback")), true);
  assert.equal(seen.some(({request}) => request.url.endsWith("/transactions/commit")), false);
});

test("drains accepted adapter work even when the callback omits await", async () => {
  const seen: Seen[] = [];
  let releasePage!: () => void;
  let signalPage!: () => void;
  const started = new Promise<void>(resolve => { signalPage = resolve; });
  const release = new Promise<void>(resolve => { releasePage = resolve; });
  const adapter = createTerraScaleAuthAdapter(client(seen, async (request, body) => {
    if (request.url.endsWith("/findPage")) { signalPage(); await release; return Response.json({record:null, matched:true, records:[]}); }
    return echoMutations(request, body);
  }), "auth")(options);
  const pending = adapter.transaction(async transaction => { void transaction.findMany({model:"user", limit:1}); });
  await started;
  assert.equal(seen.some(({request}) => request.url.endsWith("/transactions/commit")), false);
  releasePage();
  await pending;
  assert.equal(seen.some(({request}) => request.url.endsWith("/transactions/commit")), true);
});

function echoMutations(request: Request, body: unknown): Response {
  if (request.url.endsWith("/transactions/begin")) return Response.json({record:null, matched:true, transactionId:"tx#3", expiresAtMs:Date.now()+30_000});
  if (request.url.endsWith("/insertUnique")) return Response.json({matched:true, record:(body as {record:unknown}).record});
  return Response.json({record:null, matched:true});
}


test("provisions mapped core and plugin compound uniqueness before data operations", async () => {
  const seen: Seen[] = [];
  const adapter = createTerraScaleAuthAdapter(client(seen), "auth")({
    ...options,
    account: {modelName:"identities", fields:{issuer:"identity_issuer", accountId:"external_id"}},
    plugins: [{id:"compound-test", schema:{claim:{
      modelName:"claims",
      fields:{realm:{type:"string", fieldName:"realm_name"}, subject:{type:"string", fieldName:"subject_id"}},
      indexes:[{fields:["realm", "subject"], unique:true}],
    }}}],
  });
  await adapter.findMany({model:"user", limit:1});
  const indexes = seen.filter(({request}) => request.url.endsWith("/index")).map(({body}) => body);
  assert.ok(indexes.some(value => JSON.stringify(value) === JSON.stringify({model:"identities", fields:["identity_issuer", "external_id"]})));
  assert.ok(indexes.some(value => JSON.stringify(value) === JSON.stringify({model:"claims", fields:["realm_name", "subject_id"]})));
  assert.equal(seen.at(-1)?.request.url.endsWith("/findPage"), true);
});
