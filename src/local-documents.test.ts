import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "vitest";

import { TerraScaleLocalRouter } from "./local/router.js";
import { TerraScaleSqliteStore } from "./local/store.js";
import { installLocalSqliteTestEnvironment } from "./local/test-environment.js";

installLocalSqliteTestEnvironment();

const database = "test-database";
const collection = "test.collection";

function documentUrlFor(databaseName: string, id: string): string {
  return `http://127.0.0.1/api/v1/databases/${databaseName}/collections/${collection}/documents/${id}`;
}

function collectionUrlFor(databaseName: string, query = ""): string {
  return `http://127.0.0.1/api/v1/databases/${databaseName}/collections/${collection}/documents${query}`;
}

function documentUrl(id: string): string {
  return documentUrlFor(database, id);
}

function collectionUrl(query = ""): string {
  return collectionUrlFor(database, query);
}

async function put(
  router: TerraScaleLocalRouter,
  id: string,
  value: unknown,
  headers: Record<string, string> = {},
): Promise<Response> {
  return router.fetch(
    new Request(documentUrl(id), {
      method: "PUT",
      headers: { "content-type": "application/json", ...headers },
      body: JSON.stringify(value),
    }),
  );
}

async function get(router: TerraScaleLocalRouter, id: string): Promise<Response> {
  return router.fetch(new Request(documentUrl(id), { method: "GET" }));
}

async function putFor(
  router: TerraScaleLocalRouter,
  databaseName: string,
  id: string,
  value: unknown,
  headers: Record<string, string>,
): Promise<Response> {
  return router.fetch(
    new Request(documentUrlFor(databaseName, id), {
      method: "PUT",
      headers: { "content-type": "application/json", ...headers },
      body: JSON.stringify(value),
    }),
  );
}

function dataRequest(
  router: TerraScaleLocalRouter,
  databaseName: string,
  id: string,
  method: "DELETE" | "GET" | "PATCH",
  headers: Record<string, string>,
  body?: unknown,
): Promise<Response> {
  return router.fetch(
    new Request(documentUrlFor(databaseName, id), {
      method,
      headers: { ...(body === undefined ? {} : { "content-type": "application/json" }), ...headers },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    }),
  );
}

test("local documents persist values across a SQLite runtime restart", async () => {
  const directory = await mkdtemp(join(tmpdir(), "terrascale-local-documents-"));
  const databasePath = join(directory, "local.sqlite");
  try {
    const firstStore = new TerraScaleSqliteStore(databasePath);
    const first = new TerraScaleLocalRouter(firstStore);
    const written = await put(first, "doc-1", { onboarding: true, step: 1 });
    assert.equal(written.status, 200);
    const writeBody = (await written.json()) as { readonly revision: string };
    assert.match(writeBody.revision, /^local-\d+$/u);
    firstStore.close();

    const secondStore = new TerraScaleSqliteStore(databasePath);
    const second = new TerraScaleLocalRouter(secondStore);
    try {
      const response = await get(second, "doc-1");
      assert.equal(response.status, 200);
      assert.deepEqual(await response.json(), { onboarding: true, step: 1 });
      assert.match(response.headers.get("etag") ?? "", /^"local-\d+"$/u);
    } finally {
      secondStore.close();
    }
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("local documents list, merge-patch, enforce revisions, and delete", async () => {
  const directory = await mkdtemp(join(tmpdir(), "terrascale-local-documents-ops-"));
  const store = new TerraScaleSqliteStore(join(directory, "local.sqlite"));
  const router = new TerraScaleLocalRouter(store);
  try {
    for (const id of ["b-doc", "a-doc", "c-doc"]) {
      const response = await put(router, id, { id });
      assert.equal(response.status, 200);
    }
    const listed = await router.fetch(new Request(collectionUrl("?limit=2"), { method: "GET" }));
    assert.equal(listed.status, 200);
    const listBody = (await listed.json()) as {
      readonly documents: readonly { readonly id: string }[];
    };
    assert.deepEqual(
      listBody.documents.map((entry) => entry.id),
      ["a-doc", "b-doc"],
    );
    const after = await router.fetch(
      new Request(collectionUrl("?after=b-doc"), { method: "GET" }),
    );
    const afterBody = (await after.json()) as {
      readonly documents: readonly { readonly id: string }[];
    };
    assert.deepEqual(
      afterBody.documents.map((entry) => entry.id),
      ["c-doc"],
    );

    const first = await put(router, "rev-doc", { count: 1 });
    const revision = ((await first.json()) as { readonly revision: string }).revision;
    const stale = await put(router, "rev-doc", { count: 2 }, { "if-match": '"local-999"' });
    assert.equal(stale.status, 412);
    const fresh = await put(router, "rev-doc", { count: 2 }, { "if-match": `"${revision}"` });
    assert.equal(fresh.status, 200);
    const conflict = await put(router, "rev-doc", { count: 3 }, { "if-none-match": "*" });
    assert.equal(conflict.status, 412);

    const patched = await router.fetch(
      new Request(documentUrl("rev-doc"), {
        method: "PATCH",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ extra: true }),
      }),
    );
    assert.equal(patched.status, 200);
    const reread = await get(router, "rev-doc");
    assert.deepEqual(await reread.json(), { count: 2, extra: true });

    const deleted = await router.fetch(new Request(documentUrl("rev-doc"), { method: "DELETE" }));
    assert.equal(deleted.status, 204);
    assert.equal((await get(router, "rev-doc")).status, 404);
    const relisted = await router.fetch(new Request(collectionUrl(), { method: "GET" }));
    const relistedBody = (await relisted.json()) as {
      readonly documents: readonly { readonly id: string }[];
    };
    assert.ok(!relistedBody.documents.some((entry) => entry.id === "rev-doc"));
  } finally {
    store.close();
    await rm(directory, { recursive: true, force: true });
  }
});

test("required Better Auth principals isolate list, read, history, write, and delete", async () => {
  const directory = await mkdtemp(join(tmpdir(), "terrascale-local-document-auth-"));
  const store = new TerraScaleSqliteStore(join(directory, "local.sqlite"));
  const tokenA = "principal-a-session-0123456789-abcdefghijk";
  const tokenB = "principal-b-session-0123456789-abcdefghijk";
  const projectA = "project-a";
  const projectB = "project-b";
  const databaseA = "database-a";
  const databaseB = "database-b";
  const databaseAId = "database-a-id";
  const databaseBId = "database-b-id";
  const authA = { authorization: `Bearer ${tokenA}` };
  const authB = { authorization: `Bearer ${tokenB}` };
  try {
    store.put("better-auth", "session", "session-a", {
      token: tokenA,
      userId: "user-a",
      expiresAt: new Date(Date.now() + 60_000).toISOString(),
    });
    store.put("better-auth", "session", "session-b", {
      token: tokenB,
      userId: "user-b",
      expiresAt: new Date(Date.now() + 60_000).toISOString(),
    });
    for (const [projectId, userId, databaseId] of [
      [projectA, "user-a", databaseAId],
      [projectB, "user-b", databaseBId],
    ] as const) {
      store.put("projects", "local", projectId, {
        projectId,
        organizationId: `${projectId}-organization`,
        ownerUserIds: [userId],
      });
      store.put("sdk-local-database", projectId, databaseId, {
        id: databaseId,
        databaseId,
        projectId,
        name: projectId === projectA ? databaseA : databaseB,
      });
    }

    const routerA = new TerraScaleLocalRouter(store, {
      authentication: "required",
      bearerToken: tokenA,
    });
    const routerB = new TerraScaleLocalRouter(store, {
      authentication: "required",
      bearerToken: tokenB,
    });

    const legacyRouter = new TerraScaleLocalRouter(store);
    assert.equal(
      (await putFor(legacyRouter, databaseA, "legacy-name-doc", { legacy: true })).status,
      200,
    );

    const unresolvedToken = "unresolved-session-0123456789-abcdefghijk";
    const unresolvedRouter = new TerraScaleLocalRouter(store, {
      authentication: "required",
      bearerToken: unresolvedToken,
    });
    const unresolved = await putFor(
      unresolvedRouter,
      databaseA,
      "unresolved-doc",
      { owner: "unknown" },
      { authorization: `Bearer ${unresolvedToken}` },
    );
    assert.equal(unresolved.status, 403);
    assert.equal((await unresolved.json()).error.code, "principal_required");

    const expiredToken = "expired-session-0123456789-abcdefghijk";
    store.put("better-auth", "session", "expired-session", {
      token: expiredToken,
      userId: "user-a",
      expiresAt: new Date(Date.now() - 60_000).toISOString(),
    });
    const expiredRouter = new TerraScaleLocalRouter(store, {
      authentication: "required",
      bearerToken: expiredToken,
    });
    const expired = await putFor(
      expiredRouter,
      databaseA,
      "expired-doc",
      { owner: "expired" },
      { authorization: `Bearer ${expiredToken}` },
    );
    assert.equal(expired.status, 403);
    assert.equal((await expired.json()).error.code, "principal_required");

    assert.equal((await putFor(routerA, databaseA, "a-doc", { owner: "a" }, authA)).status, 200);
    assert.equal((await putFor(routerB, databaseB, "b-doc", { owner: "b" }, authB)).status, 200);
    assert.equal((await putFor(routerA, databaseAId, "id-doc", { owner: "a-id" }, authA)).status, 200);

    const ownedList = await routerA.fetch(new Request(collectionUrlFor(databaseA), { headers: authA }));
    assert.equal(ownedList.status, 200);
    assert.deepEqual((await ownedList.json()).documents.map((item: { id: string }) => item.id), ["a-doc", "id-doc"]);
    assert.equal((await dataRequest(routerA, databaseA, "id-doc", "GET", authA)).status, 200);
    assert.equal((await dataRequest(routerA, databaseAId, "a-doc", "GET", authA)).status, 200);

    const deniedList = await routerA.fetch(new Request(collectionUrlFor(databaseB), { headers: authA }));
    assert.equal(deniedList.status, 403);
    assert.equal((await deniedList.json()).error.code, "database_forbidden");

    const ownedSql = await routerA.fetch(new Request(
      `http://127.0.0.1/api/v1/databases/${databaseA}/sql`,
      {
        method: "POST",
        headers: { ...authA, "content-type": "application/json" },
        body: JSON.stringify({ sql: "select 1" }),
      },
    ));
    assert.equal(ownedSql.status, 200);
    const deniedSql = await routerA.fetch(new Request(
      `http://127.0.0.1/api/v1/databases/${databaseB}/sql`,
      {
        method: "POST",
        headers: { ...authA, "content-type": "application/json" },
        body: JSON.stringify({ sql: "select 1" }),
      },
    ));
    assert.equal(deniedSql.status, 403);
    assert.equal((await deniedSql.json()).error.code, "database_forbidden");

    assert.equal((await dataRequest(routerA, databaseA, "a-doc", "GET", authA)).status, 200);
    assert.equal((await dataRequest(routerA, databaseB, "b-doc", "GET", authA)).status, 403);
    const ownedHistory = await routerA.fetch(new Request(
      `${documentUrlFor(databaseA, "a-doc")}/history`,
      { headers: authA },
    ));
    assert.equal(ownedHistory.status, 200);
    const deniedHistory = await routerA.fetch(new Request(
      `${documentUrlFor(databaseB, "b-doc")}/history`,
      { headers: authA },
    ));
    assert.equal(deniedHistory.status, 403);

    assert.equal((await dataRequest(routerA, databaseA, "a-doc", "PATCH", authA, { changed: true })).status, 200);
    assert.equal((await dataRequest(routerA, databaseB, "b-doc", "PATCH", authA, { changed: true })).status, 403);
    assert.equal((await dataRequest(routerA, databaseB, "b-doc", "DELETE", authA)).status, 403);
    assert.equal((await dataRequest(routerB, databaseB, "b-doc", "GET", authB)).status, 200);

    const revokedReplayHeaders = { ...authA, "idempotency-key": "document-replay-revoked" };
    assert.equal(
      (await putFor(routerA, databaseA, "revoked-replay-doc", { value: 1 }, revokedReplayHeaders)).status,
      200,
    );
    assert.equal(store.delete("better-auth", "session", "session-a"), true);
    const revokedReplay = await putFor(
      routerA,
      databaseA,
      "revoked-replay-doc",
      { value: 1 },
      revokedReplayHeaders,
    );
    assert.equal(revokedReplay.status, 403);
    assert.equal((await revokedReplay.json()).error.code, "principal_required");
    store.put("better-auth", "session", "session-a", {
      token: tokenA,
      userId: "user-a",
      expiresAt: new Date(Date.now() + 60_000).toISOString(),
    });

    const expiredReplayHeaders = { ...authB, "idempotency-key": "document-replay-expired" };
    assert.equal(
      (await putFor(routerB, databaseB, "expired-replay-doc", { value: 1 }, expiredReplayHeaders)).status,
      200,
    );
    store.put("better-auth", "session", "session-b", {
      token: tokenB,
      userId: "user-b",
      expiresAt: new Date(Date.now() - 60_000).toISOString(),
    });
    const expiredReplay = await putFor(
      routerB,
      databaseB,
      "expired-replay-doc",
      { value: 1 },
      expiredReplayHeaders,
    );
    assert.equal(expiredReplay.status, 403);
    assert.equal((await expiredReplay.json()).error.code, "principal_required");
    store.put("better-auth", "session", "session-b", {
      token: tokenB,
      userId: "user-b",
      expiresAt: new Date(Date.now() + 60_000).toISOString(),
    });

    const membershipReplayHeaders = { ...authA, "idempotency-key": "document-replay-membership" };
    assert.equal(
      (await putFor(routerA, databaseA, "membership-replay-doc", { value: 1 }, membershipReplayHeaders)).status,
      200,
    );
    store.put("projects", "local", projectA, {
      projectId: projectA,
      organizationId: `${projectA}-organization`,
      ownerUserIds: [],
    });
    const membershipReplay = await putFor(
      routerA,
      databaseA,
      "membership-replay-doc",
      { value: 1 },
      membershipReplayHeaders,
    );
    assert.equal(membershipReplay.status, 403);
    assert.equal((await membershipReplay.json()).error.code, "database_forbidden");
    store.put("projects", "local", projectA, {
      projectId: projectA,
      organizationId: `${projectA}-organization`,
      ownerUserIds: ["user-a"],
    });

    const wrongProject = await routerA.fetch(new Request(collectionUrlFor(databaseA), {
      headers: { ...authA, "x-terrascale-project-id": projectB },
    }));
    assert.equal(wrongProject.status, 403);
    assert.equal((await dataRequest(routerA, databaseA, "a-doc", "DELETE", authA)).status, 204);
    assert.equal((await dataRequest(routerA, databaseA, "a-doc", "GET", authA)).status, 404);

    assert.equal(store.delete("sdk-local-database", projectA, databaseAId), true);
    const replacementId = "database-recreated-id";
    store.put("sdk-local-database", projectB, replacementId, {
      id: replacementId,
      databaseId: replacementId,
      projectId: projectB,
      name: databaseA,
    });
    assert.equal(
      (await putFor(routerB, databaseA, "replacement-doc", { owner: "b-recreated" }, authB)).status,
      200,
    );
    const replacementList = await routerB.fetch(new Request(collectionUrlFor(databaseA), { headers: authB }));
    assert.equal(replacementList.status, 200);
    assert.deepEqual(
      (await replacementList.json()).documents.map((item: { id: string }) => item.id),
      ["replacement-doc"],
    );
    const oldNameAccess = await routerA.fetch(new Request(collectionUrlFor(databaseA), { headers: authA }));
    assert.equal(oldNameAccess.status, 403);
    assert.equal((await oldNameAccess.json()).error.code, "database_forbidden");
    assert.equal((await dataRequest(routerB, replacementId, "replacement-doc", "GET", authB)).status, 200);
    assert.equal((await dataRequest(routerB, replacementId, "id-doc", "GET", authB)).status, 404);
    const legacyStored = store.get<{ readonly value: unknown }>(
      "sdk-local/documents",
      `${databaseA}/${collection}`,
      "legacy-name-doc",
    );
    assert.deepEqual(legacyStored?.value, { legacy: true });
  } finally {
    store.close();
    await rm(directory, { recursive: true, force: true });
  }
});
