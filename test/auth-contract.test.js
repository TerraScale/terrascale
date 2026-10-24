import { describe, expect, test } from "vitest";
import { createTerraBaseV1Client, V1ForbiddenScopeError, V1InvalidKeyError } from "../src/index.js";
import { createTsAuthClient } from "../src/ts-auth.js";
import { v1Routes, shapeV1Routes } from "../src/v1/routes.js";
import { managementV1Routes } from "../src/v1/management-routes.js";
import { fileStorageRoutes } from "../src/v1/file-storage-routes.js";

// ts-auth -> TerraBase platform token contract (terrascale TerraBaseOptions.PlatformAuthIssuer/Audience,
// ApiKeys.TerraBaseScopes, IApiKeyStore ApiKeyScopes). Fixtures only; no live service.
const origin = "https://auth.terrascale.tech";
const issuer = "https://auth.terrascale.tech/terrascale";
const audience = "https://api.terrascale.tech";
const scopes = ["databases:read", "databases:write", "documents:read", "documents:write"];
const clientId = "01ARZ3NDEKTSV4RRFFQ6000002";
const project = "01ARZ3NDEKTSV4RRFFQ69G5FA1";

/** @param {(request: Request) => Response} respond @param {Record<string, unknown>} [overrides] */
function platform(respond, overrides = {}) {
  /** @type {Request[]} */
  const requests = [];
  const client = createTsAuthClient({
    apiBaseUrl: origin,
    instanceSlug: "terrascale",
    accessToken: "sk_test_management_key",
    fetch: async (input, init) => {
      const request = new Request(input, init);
      requests.push(request.clone());
      return respond(request);
    },
    ...overrides,
  });
  return { client, requests };
}

const token = { access_token: "header.payload.signature", token_type: "Bearer", expires_in: 900, scope: "databases:read documents:read" };

describe("platform token contract", () => {
  test("every runtime TerraBase route requires one of the four contract scopes", () => {
    // readShape is the historical shapes:read release route; the runtime shape reader uses readFreeShape.
    const runtime = [...v1Routes.filter(route => route.operation !== "readShape"), ...shapeV1Routes, ...fileStorageRoutes,
      ...managementV1Routes.filter(route => route.permission !== "anonymous" && route.permission !== "platform-admin")];
    for (const route of runtime) {
      expect(scopes, route.operation).toContain(route.permission);
      for (const extra of route.additionalPermissions ?? []) expect(scopes, route.operation).toContain(extra);
    }
  });

  test("discovery and token requests go to the one global issuer, never a regional or proxy host", async () => {
    const { client, requests } = platform(() => Response.json(token));
    await client.oidc.discovery();
    const result = await client.oidc.token(clientId, "secret", { grant_type: "client_credentials", resource: audience, scope: "databases:read documents:read" });
    expect(result.ok).toBe(true);
    expect(requests.map(request => request.url)).toEqual([
      `${issuer}/.well-known/openid-configuration`,
      `${issuer}/oauth/token`,
    ]);
    expect(requests[0]?.headers.get("authorization")).toBeNull();
    const form = new URLSearchParams(await requests[1]?.text());
    expect(form.get("grant_type")).toBe("client_credentials");
    expect(form.get("resource")).toBe(audience);
    expect(form.getAll("scope")).toEqual(["databases:read documents:read"]);
    expect(form.get("client_id")).toBe(clientId);
  });

  test("client_credentials sends exactly the requested scopes and never defaults or broadens them", async () => {
    const { client, requests } = platform(() => Response.json(token));
    for (const scope of ["documents:read", scopes.join(" ")]) {
      await client.oidc.token(clientId, "secret", { grant_type: "client_credentials", resource: audience, scope });
      expect(new URLSearchParams(await requests.at(-1)?.text()).get("scope")).toBe(scope);
    }
    const sent = requests.length;
    for (const scope of [undefined, "", /** @type {never} */ (scopes), "documents:read documents:read"])
      expect(() => client.oidc.token(clientId, "secret", { grant_type: "client_credentials", resource: audience, scope: /** @type {string} */ (scope) })).toThrow(TypeError);
    expect(() => client.oidc.token(clientId, "secret", /** @type {never} */ ({ grant_type: "client_credentials", resource: audience, scope: "documents:read", scopes }))).toThrow(TypeError);
    // A refresh without scope keeps the stored grant; the SDK adds none.
    await client.oidc.token(clientId, "secret", { grant_type: "refresh_token", refresh_token: "rt" });
    expect(new URLSearchParams(await requests.at(-1)?.text()).has("scope")).toBe(false);
    expect(requests).toHaveLength(sent + 1);
  });

  test("resource introspection accepts only the exact issuer and audience", async () => {
    const now = Math.floor(Date.now() / 1000);
    const claims = { active: true, token_type: "Bearer", iss: issuer, sub: "01ARZ3NDEKTSV4RRFFQ6000001", aud: audience, client_id: clientId,
      scope: "documents:read", exp: now + 60, iat: now - 10, sid: "01ARZ3NDEKTSV4RRFFQ6000004", principal_kind: "user" };
    const credentials = { issuer, audience, introspection_client_id: "01ARZ3NDEKTSV4RRFFQ6000003", introspection_secret: "secret" };
    /** @param {Record<string, unknown>} response */
    const introspect = async response => {
      const { client, requests } = platform(() => Response.json(response));
      const result = await client.oidc.introspectResource(credentials, "opaque");
      expect(requests.map(request => new URL(request.url).host)).toEqual(["auth.terrascale.tech"]);
      return result.ok;
    };
    expect(await introspect(claims)).toBe(true);
    for (const iss of [`${issuer}/`, "https://gru.auth.terrascale.tech/terrascale", origin])
      expect(await introspect({ ...claims, iss }), iss).toBe(false);
    for (const aud of [`${audience}/`, "https://gru.api.terrascale.tech"])
      expect(await introspect({ ...claims, aud }), aud).toBe(false);
  });

  test("ts-auth 401 refreshes the token once; 403 is an authorization failure, never re-auth", async () => {
    for (const [status, kind, calls] of /** @type {const} */ ([[401, "authentication", 2], [403, "authorization", 1]])) {
      let resolved = 0;
      const { client, requests } = platform(() => Response.json({ error: { code: "x" } }, { status }), {
        accessToken: () => `sk_test_${++resolved}`,
      });
      const result = await client.users.list();
      expect(result.ok).toBe(false);
      if (!result.ok) expect(result.error.kind).toBe(kind);
      expect(requests).toHaveLength(calls);
    }
  });

  test("TerraBase 401 and 403 surface as distinct typed errors without retry", async () => {
    for (const [status, code, type] of /** @type {const} */ ([[401, "invalid_key", V1InvalidKeyError], [403, "forbidden_scope", V1ForbiddenScopeError]])) {
      let calls = 0;
      const client = createTerraBaseV1Client({ project, token: "header.payload.signature", baseUrl: "https://api.terrascale.tech",
        fetch: async () => (calls++, Response.json({ error: { code, message: "denied" } }, { status })) });
      const error = await client.database("app").collection("users").get("01ARZ3NDEKTSV4RRFFQ69G5FAV").catch(error => error);
      expect(error).toBeInstanceOf(type);
      expect(error).toMatchObject({ status, code });
      expect(calls).toBe(1);
    }
  });
});
