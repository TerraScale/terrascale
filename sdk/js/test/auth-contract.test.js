import { describe, expect, test } from "vitest";
import { createTerraBaseV1Client, V1ForbiddenScopeError, V1InvalidKeyError } from "../src/index.js";
import { createTsAuthClient } from "../src/ts-auth.js";

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
  // Public producer platform-api.openapi.yaml ClientCredentialsGrant / PlatformGrants.ApiKeyAsync.
  test("project API keys send the explicit project and optional subset scope without resource", async () => {
    for (const scope of [undefined, "documents:read"]) {
      const response = { ...token, expires_in: 60, scope: scope ?? token.scope };
      const { client, requests } = platform(() => Response.json(scope === undefined ? response : { access_token: token.access_token, token_type: "Bearer", expires_in: 60 }));
      const result = await client.oidc.token(clientId, "secret&+", { grant_type: "client_credentials", project, ...(scope === undefined ? {} : { scope }) });
      expect(result).toMatchObject({ ok: true, status: 200, value: response });
      expect(requests).toHaveLength(1);
      const request = /** @type {Request} */ (requests[0]);
      expect([request.method, request.url]).toEqual(["POST", `${issuer}/oauth/token`]);
      expect(request.headers.get("authorization")).toBeNull();
      expect(request.headers.get("content-type")).toBe("application/x-www-form-urlencoded");
      expect(Object.fromEntries(new URLSearchParams(await request.text()))).toEqual({ client_id: clientId, client_secret: "secret&+", grant_type: "client_credentials", project, ...(scope === undefined ? {} : { scope }) });
    }
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
