import { expect, test } from "vitest";
import { createTsAuthClient, TsAuthUserPatch } from "../src/ts-auth.js";
import { entity, fixture, json } from "./helpers/v1-http.js";

test.each(["GET", "POST", "PATCH", "DELETE"])("hosted %s denial is never replayed with a changed credential", async method => {
  const { baseUrl, requests } = await fixture((_request, response) => json(response, { error: { code: "invalid_token", message: "Denied" } }, 401));
  let credentials = 0;
  const client = createTsAuthClient({ apiBaseUrl: baseUrl, instanceSlug: "q11", maxRetryAttempts: 2, accessToken: () => `q11-fixture-${++credentials}` });
  const result = await (method === "POST" ? client.users.create({ email: "q11@example.test" }) :
    method === "PATCH" ? client.users.update(entity, new TsAuthUserPatch().withName("q11")) :
      method === "DELETE" ? client.users.delete(entity) : client.users.get(entity));
  expect(result.ok).toBe(false);
  expect(result.status).toBe(401);
  expect(requests.map(request => request.method)).toEqual([method]);
  expect(credentials).toBe(1);
});

test.each(["GET", "POST", "PATCH", "DELETE", "FORM"])("native hosted %s preserves bytes and never replays mutations on421", async method => {
  const { baseUrl, requests } = await fixture((_request, response) => json(response, { error: { code: "wrong_region" } }, 421));
  const client = createTsAuthClient({ apiBaseUrl: baseUrl, instanceSlug: "q11", accessToken: "q11-fixture", maxRetryAttempts: 0 });
  const result = await (method === "POST" ? client.users.create({ email: "q11@example.test" }) :
    method === "PATCH" ? client.users.update(entity, new TsAuthUserPatch().withName("q11")) :
      method === "DELETE" ? client.users.delete(entity) : method === "FORM" ? client.oidc.token(entity, "q11-fixture", { grant_type: "client_credentials", resource: "https://api.terrascale.tech", scope: "documents:read" }) : client.users.get(entity));
  expect(result.status).toBe(421);
  expect(requests).toHaveLength(method === "GET" ? 2 : 1);
  if (method === "FORM") {
    expect(requests[0]?.headers["content-type"]).toBe("application/x-www-form-urlencoded");
    const body = new URLSearchParams(requests[0]?.body);
    expect(body.get("grant_type")).toBe("client_credentials");
    expect(body.get("resource")).toBe("https://api.terrascale.tech");
    expect(body.get("scope")).toBe("documents:read");
  } else if (method === "POST" || method === "PATCH") {
    expect(requests[0]?.headers["content-type"]).toBe("application/json");
    expect(JSON.parse(requests[0]?.body ?? "")).toEqual(method === "POST" ? { email: "q11@example.test" } : { name: "q11" });
  } else expect(requests.every(request => request.body === "")).toBe(true);
});
