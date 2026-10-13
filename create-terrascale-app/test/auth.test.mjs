import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import ts from "typescript";

const source = await readFile(new URL("../template/auth.ts", import.meta.url), "utf8");
const { outputText } = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 } });
const { createStarterCredential, readOAuthTokens } = await import(`data:text/javascript;base64,${Buffer.from(outputText + "\n//# sourceURL=starter-auth.js").toString("base64")}`);
const config = { authBaseUrl: "https://auth.example.test", apiBaseUrl: "https://api.example.test", projectId: "project", databaseId: "branch" };
const json = (body) => new Response(JSON.stringify(body), { headers: { "content-type": "application/json" } });

test("refresh rotates once for concurrent callers and subsequent renewal uses the rotated token", async (t) => {
  let now = 100_000;
  t.mock.method(Date, "now", () => now);
  const refreshes = [];
  const delegations = [];
  t.mock.method(globalThis, "fetch", async (url, request) => {
    if (new URL(url).pathname.endsWith("/token")) {
      const params = request.body;
      assert.equal(params.get("grant_type"), "refresh_token");
      assert.equal(params.get("client_id"), "terrascale-starter-web");
      assert.equal(params.get("resource"), config.apiBaseUrl);
      assert.equal(params.has("code_verifier"), false);
      refreshes.push(params.get("refresh_token"));
      return json({ access_token: `access-${refreshes.length}`, refresh_token: `refresh-${refreshes.length}`, expires_in: 60 });
    }
    delegations.push(request.headers.authorization);
    assert.deepEqual(JSON.parse(request.body), { projectId: "project", databaseId: "branch", collection: "posts", actions: ["read", "write"] });
    return json({ accessToken: `delegated-${delegations.length}`, expiresIn: 60 });
  });
  const credential = createStarterCredential({ accessToken: "expired", refreshToken: "refresh-0", renewAt: 0 }, config);
  assert.deepEqual(await Promise.all([credential.accessToken(), credential.accessToken()]), ["delegated-1", "delegated-1"]);
  assert.equal(await credential.accessToken(), "delegated-1");
  now += 51_000;
  assert.deepEqual(await Promise.all([credential.accessToken(), credential.accessToken()]), ["delegated-2", "delegated-2"]);
  assert.deepEqual(refreshes, ["refresh-0", "refresh-1"]);
  assert.deepEqual(delegations, ["Bearer access-1", "Bearer access-2"]);
  credential.disconnect();
  await assert.rejects(credential.accessToken(), /Disconnected/);
});

test("an uncertain refresh response is never replayed", async (t) => {
  let requests = 0;
  t.mock.method(globalThis, "fetch", async () => { requests++; throw new TypeError("connection lost"); });
  const credential = createStarterCredential({ accessToken: "expired", refreshToken: "single-use", renewAt: 0 }, config);
  await assert.rejects(credential.accessToken(), /connection lost/);
  await assert.rejects(credential.accessToken(), /Sign-in expired/);
  assert.equal(requests, 1);
});

test("disconnect aborts pending delegation and does not install a late token", async (t) => {
  let finish;
  let signal;
  t.mock.method(globalThis, "fetch", (_url, request) => {
    signal = request.signal;
    return new Promise((resolve) => { finish = resolve; });
  });
  const credential = createStarterCredential({ accessToken: "active", refreshToken: "refresh", renewAt: Date.now() + 60_000 }, config);
  const pending = credential.accessToken();
  credential.disconnect();
  assert.equal(signal.aborted, true);
  finish(json({ accessToken: "late", expiresIn: 60 }));
  await assert.rejects(pending, /Disconnected/);
  await assert.rejects(credential.accessToken(), /Disconnected/);
});

test("OAuth requires rotating refresh credentials and finite positive expiry", async () => {
  for (const body of [
    { access_token: "a", expires_in: 60 },
    { access_token: "a", refresh_token: "r", expires_in: 0 },
    { access_token: "a", refresh_token: "r", expires_in: "60" },
  ]) await assert.rejects(readOAuthTokens(json(body)), /could not be renewed/);
});
