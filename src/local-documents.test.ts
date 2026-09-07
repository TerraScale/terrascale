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

function documentUrl(id: string): string {
  return `http://127.0.0.1/api/v1/databases/${database}/collections/${collection}/documents/${id}`;
}

function collectionUrl(query = ""): string {
  return `http://127.0.0.1/api/v1/databases/${database}/collections/${collection}/documents${query}`;
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
