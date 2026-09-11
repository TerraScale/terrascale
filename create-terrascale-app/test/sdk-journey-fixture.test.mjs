import assert from "node:assert/strict";
import test from "node:test";
import { createFixtureServer, runJourney } from "../qualification/sdk-journey-fixture.mjs";

test("fixture enforces account, verification, onboarding, and synthetic-key ordering", async () => {
  const fixture = createFixtureServer({ runId: "ordering-test" });
  const port = await fixture.listen();
  const origin = `http://127.0.0.1:${port}`;
  const post = (path, value) => fetch(`${origin}${path}`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(value) });
  try {
    assert.equal((await post("/__fixture__/onboarding", {})).status, 403);
    const signup = await post("/__fixture__/signup", { email: "test@fixture.invalid" });
    assert.equal(signup.status, 201);
    assert.equal((await post("/__fixture__/onboarding", {})).status, 403);
    assert.equal((await post("/__fixture__/verify", { token: "verify-ordering-test" })).status, 200);
    assert.equal((await post("/__fixture__/onboarding", {})).status, 201);
    const issued = await post("/__fixture__/api-key", {});
    assert.equal(issued.status, 201);
    const { apiKey } = await issued.json();
    const put = await fetch(`${origin}/api/v1/databases/db/collections/c/documents/id`, { method: "PUT", headers: { authorization: `Bearer ${apiKey}`, "content-type": "application/json" }, body: JSON.stringify({ sdk: "fixture" }) });
    assert.equal(put.status, 200);
    const get = await fetch(`${origin}/api/v1/databases/db/collections/c/documents/id`, { headers: { authorization: `Bearer ${apiKey}` } });
    assert.deepEqual(await get.json(), { sdk: "fixture" });
    assert.equal((await fetch(`${origin}/api/v1/databases/db/collections/c/documents/other`)).status, 401);
    assert.deepEqual(fixture.state().documentIds, ["c/id"]);
    assert.equal("value" in fixture.state(), false);
  } finally {
    await fixture.close();
  }
});

test("fixture journey fails when a driver does not perform an authenticated write", async () => {
  await assert.rejects(
    () => runJourney({
      runId: "no-op-driver",
      drivers: Object.fromEntries(["typescript", "go", "rust", "dotnet", "cli"].map((name) => [name, "node -e 'process.exit(0)'"])),
    }),
    /typescript driver exited successfully without writing its required document/u,
  );
});
