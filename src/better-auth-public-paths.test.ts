import { strict as assert } from "node:assert";
import { test } from "vitest";

import { AuthPersistenceClient } from "./auth-persistence.js";
import { createTerraScaleAuthAdapter } from "./better-auth-factory.js";

test("maps conditional updates and one-time consumption to named public auth routes", async () => {
  const requests: Request[] = [];
  const client = new AuthPersistenceClient({
    apiBaseUrl: "https://api.example.test",
    fetch: async (input, init) => {
      const request = new Request(input, init);
      requests.push(request);
      const path = new URL(request.url).pathname;
      if (path.endsWith("/index")) return Response.json({ matched: true, record: null });
      if (path.endsWith("/findPage")) return Response.json({ matched: true, records: [] });
      return Response.json({ matched: true, record: { id: "result" } });
    },
  });
  const adapter = createTerraScaleAuthAdapter(client, "auth")({
    appName: "public-paths",
    secret: "test-secret-test-secret-test-secret",
  });

  await adapter.update<{ id: string }>({
    model: "user",
    where: [{ field: "id", value: "u1" }],
    update: { email: "updated@example.test" },
  });
  await adapter.consumeOne<{ id: string }>({
    model: "verification",
    where: [{ field: "identifier", value: "token" }],
  });

  const paths = requests.map(request => new URL(request.url).pathname);
  assert.equal(paths.at(-2)?.endsWith("/updateOne"), true);
  assert.equal(paths.at(-1)?.endsWith("/consumeMatching"), true);
  const update = requests.at(-2)!;
  const updateBody = await update.clone().json() as { model: string; predicates: unknown; update: unknown };
  assert.equal(updateBody.model, "user");
  assert.deepEqual(updateBody.predicates, [{ field: "id", value: "u1" }]);
  assert.deepEqual(updateBody.update, {
    email: "updated@example.test",
    updatedAt: (updateBody.update as { updatedAt: string }).updatedAt,
  });
  assert.match((updateBody.update as { updatedAt: string }).updatedAt, /^\d{4}-\d{2}-\d{2}T/u);
  assert.ok(update.headers.get("idempotency-key"));
  assert.ok(requests.at(-1)!.headers.get("idempotency-key"));
});
