import assert from "node:assert/strict";
import { describe, test } from "vitest";

import { createTsAuthClient } from "./ts-auth.js";
/** @import { TsAuthClient } from "./ts-auth.js" */

const user = {
  id: "01ARZ3NDEKTSV4RRFFQ6000011",
  email: "ada@acme.test",
  email_verified: true,
  username: "ada",
  name: "Ada",
  banned: false,
  created_at: 1_788_220_800,
  public_metadata: {},
};

const discoveryDoc = {
  issuer: "https://auth.acme.test/acme",
  authorization_endpoint: "https://auth.acme.test/acme/oauth/authorize",
  token_endpoint: "https://auth.acme.test/acme/oauth/token",
  userinfo_endpoint: "https://auth.acme.test/acme/oauth/userinfo",
  jwks_uri: "https://auth.acme.test/acme/.well-known/jwks.json",
  revocation_endpoint: "https://auth.acme.test/acme/oauth/revoke",
  introspection_endpoint: "https://auth.acme.test/acme/oauth/introspect",
  end_session_endpoint: "https://auth.acme.test/acme/oauth/end-session",
  response_types_supported: ["code"],
  grant_types_supported: [
    "authorization_code",
    "refresh_token",
    "client_credentials",
  ],
  code_challenge_methods_supported: ["S256"],
  subject_types_supported: ["public"],
  id_token_signing_alg_values_supported: ["ES256"],
  token_endpoint_auth_methods_supported: ["client_secret_post"],
  scopes_supported: ["openid"],
};

/**
 * @param {unknown} value
 * @param {number} [status=200]
 * @returns {Response}
 */
function json(value, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "content-type": "application/json" },
  });
}

/**
 * @param {Request[]} requests
 * @param {(request: Request, index: number) => Response} respond
 * @returns {typeof fetch}
 */
function capture(requests, respond) {
  return async (input) => {
    const request = new Request(input);
    requests.push(request.clone());
    return respond(request, requests.length - 1);
  };
}

/**
 * @param {(request: Request, index: number) => Response} respond
 * @param {Request[]} requests
 * @param {Record<string, unknown>} [overrides={}]
 * @returns {TsAuthClient}
 */
function tsAuthClient(respond, requests, overrides = {}) {
  return createTsAuthClient({
    apiBaseUrl: "https://auth.acme.test",
    accessToken: "sk_test_management_key",
    instanceSlug: "acme",
    fetch: capture(requests, respond),
    ...overrides,
  });
}

describe("ts-auth client", () => {
  test("projects list and disable users with the sign-in bearer", async () => {
    /** @type {Request[]} */
    const requests = [];
    const projectUser = {
      id: "01ARZ3NDEKTSV4RRFFQ6000011",
      email: "ada@acme.test",
      created_at: "2026-11-08T00:00:00Z",
      disabled: false,
    };
    const tsAuth = tsAuthClient(
      (_request, index) => json(index === 0 ? { items: [projectUser], next: null } : projectUser),
      requests,
    );
    const signIn = "header.payload.signature";

    const listed = await tsAuth.projects.listUsers(
      signIn,
      "01ARZ3NDEKTSV4RRFFQ6000042",
      { search: "ada", limit: 2, after: "cursor" },
    );
    assert.equal(listed.ok, true);
    assert.equal(
      new URL(/** @type {Request} */ (requests[0]).url).pathname,
      "/v1/01ARZ3NDEKTSV4RRFFQ6000042/users",
    );
    assert.equal(
      new URL(/** @type {Request} */ (requests[0]).url).search,
      "?q=ada&limit=2&after=cursor",
    );
    assert.equal(
      /** @type {Request} */ (requests[0]).headers.get("authorization"),
      `Bearer ${signIn}`,
    );
    if (listed.ok) {
      assert.equal(listed.value.items[0]?.email, "ada@acme.test");
      assert.equal(listed.value.next, null);
    }

    const disabled = await tsAuth.projects.disableUser(
      signIn,
      "01ARZ3NDEKTSV4RRFFQ6000042",
      "01ARZ3NDEKTSV4RRFFQ6000011",
    );
    assert.equal(disabled.ok, true);
    assert.equal(
      new URL(/** @type {Request} */ (requests[1]).url).pathname,
      "/v1/01ARZ3NDEKTSV4RRFFQ6000042/users/01ARZ3NDEKTSV4RRFFQ6000011/disable",
    );
    assert.equal(/** @type {Request} */ (requests[1]).method, "POST");
    assert.equal(
      /** @type {Request} */ (requests[1]).headers.get("authorization"),
      `Bearer ${signIn}`,
    );
  });

  test("sends exact management routes, bearer, and JSON bodies", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient(
      (_request, index) =>
        json(
          index === 1
            ? user
            : index === 4
              ? {
                  id: "01ARZ3NDEKTSV4RRFFQ6000007",
                  name: "CI",
                  api_key: "01ARZ3NDEKTSV4RRFFQ6000007_" + "b".repeat(52),
                  scopes: ["users:read"],
                  previous_secret_expires_at: 1_788_230_400,
                }
              : { data: [user] },
        ),
      requests,
    );

    const listed = await tsAuth.users.list({
      limit: 100,
      startingAfter: "01ARZ3NDEKTSV4RRFFQ6000011",
    });
    assert.equal(listed.ok, true);
    assert.equal(
      new URL(/** @type {Request} */ (requests[0]).url).pathname,
      "/v1/acme/users",
    );
    assert.equal(
      new URL(/** @type {Request} */ (requests[0]).url).search,
      "?limit=100&starting_after=01ARZ3NDEKTSV4RRFFQ6000011",
    );
    assert.equal(/** @type {Request} */ (requests[0]).method, "GET");
    assert.equal(
      /** @type {Request} */ (requests[0]).headers.get("authorization"),
      "Bearer sk_test_management_key",
    );
    assert.equal(
      /** @type {Request} */ (requests[0]).headers.get("accept"),
      "application/json",
    );
    assert.equal(
      /** @type {Request} */ (requests[0]).headers.get("content-type"),
      null,
    );
    if (listed.ok) assert.equal(listed.value.data[0]?.id, "01ARZ3NDEKTSV4RRFFQ6000011");

    const created = await tsAuth.users.create({
      email: "grace@acme.test",
      password: "hunter2000",
    });
    assert.equal(created.ok, true);
    assert.equal(
      new URL(/** @type {Request} */ (requests[1]).url).pathname,
      "/v1/acme/users",
    );
    assert.equal(/** @type {Request} */ (requests[1]).method, "POST");
    assert.equal(
      /** @type {Request} */ (requests[1]).headers.get("content-type"),
      "application/json",
    );
    assert.deepEqual(
      JSON.parse(await /** @type {Request} */ (requests[1]).text()),
      {
        email: "grace@acme.test",
        password: "hunter2000",
      },
    );

    const revoked = await tsAuth.sessions.revoke("01ARZ3NDEKTSV4RRFFQ6000010");
    assert.equal(revoked.ok, true);
    assert.equal(
      new URL(/** @type {Request} */ (requests[2]).url).pathname,
      "/v1/acme/sessions/01ARZ3NDEKTSV4RRFFQ6000010/revoke",
    );
    assert.equal(/** @type {Request} */ (requests[2]).method, "POST");

    const deleted = await tsAuth.apiKeys.delete("01ARZ3NDEKTSV4RRFFQ6000007");
    assert.equal(deleted.ok, true);
    assert.equal(
      new URL(/** @type {Request} */ (requests[3]).url).pathname,
      "/v1/acme/api-keys/01ARZ3NDEKTSV4RRFFQ6000007",
    );
    assert.equal(/** @type {Request} */ (requests[3]).method, "DELETE");

    const rotated = await tsAuth.apiKeys.rotate(
      "01ARZ3NDEKTSV4RRFFQ6000007",
      { graceSeconds: 3600 },
    );
    assert.equal(rotated.ok, true);
    if (rotated.ok) {
      assert.equal(
        rotated.value.api_key,
        "01ARZ3NDEKTSV4RRFFQ6000007_" + "b".repeat(52),
      );
      assert.equal(rotated.value.previous_secret_expires_at, 1_788_230_400);
    }
    assert.equal(
      new URL(/** @type {Request} */ (requests[4]).url).pathname,
      "/v1/acme/api-keys/01ARZ3NDEKTSV4RRFFQ6000007/rotate",
    );
    assert.equal(/** @type {Request} */ (requests[4]).method, "POST");
    assert.deepEqual(
      JSON.parse(await /** @type {Request} */ (requests[4]).text()),
      { previous_secret_ttl: 3600 },
    );
  });

  test("maps nested management and flat OAuth error envelopes", async () => {
    /** @type {Request[]} */
    const nestedRequests = [];
    const nested = tsAuthClient(
      () =>
        json(
          { error: { code: "user_not_found", message: "No such user." } },
          404,
        ),
      nestedRequests,
    );
    const missing = await nested.users.get("01ARZ3NDEKTSV4RRFFQ6000014");
    assert.equal(missing.ok, false);
    if (!missing.ok) {
      assert.equal(missing.status, 404);
      assert.equal(missing.error.kind, "not-found");
      assert.equal(missing.error.code, "user_not_found");
      assert.equal(missing.error.message, "No such user.");
    }

    /** @type {Request[]} */

    const flatRequests = [];
    const flat = tsAuthClient(
      () =>
        json(
          {
            error: "invalid_client",
            error_description: "Client authentication failed.",
          },
          401,
        ),
      flatRequests,
    );
    const token = await flat.oidc.token("01ARZ3NDEKTSV4RRFFQ6000002", "cs_wrong", {
      grant_type: "client_credentials",
      resource: "https://api.example.test/management",
      scope: "management:read",
    });
    assert.equal(token.ok, false);
    if (!token.ok) {
      assert.equal(token.status, 401);
      assert.equal(token.error.kind, "authentication");
      assert.equal(token.error.code, "invalid_client");
      assert.equal(token.error.message, "Client authentication failed.");
    }
  });

  test("retries safe reads once and never retries mutations", async () => {
    /** @type {Request[]} */
    const readRequests = [];
    const retried = tsAuthClient(
      (_request, index) =>
        index === 0
          ? json({ error: { code: "unavailable", message: "Try again." } }, 503)
          : json({ data: [user] }),
      readRequests,
      { maxRetryAttempts: 1, retryDelayMs: 0 },
    );
    const listed = await retried.users.list();
    assert.equal(listed.ok, true);
    assert.equal(readRequests.length, 2);

    /** @type {Request[]} */

    const mutationRequests = [];
    const failing = tsAuthClient(
      () =>
        json({ error: { code: "unavailable", message: "Try again." } }, 503),
      mutationRequests,
      { maxRetryAttempts: 1, retryDelayMs: 0 },
    );
    const created = await failing.apiKeys.create({ name: "ci" });
    assert.equal(created.ok, false);
    if (!created.ok) assert.equal(created.error.kind, "server");
    assert.equal(mutationRequests.length, 1);
  });

  test("posts OIDC form bodies and keeps public discovery routes anonymous", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient((request) => {
      const path = new URL(request.url).pathname;
      if (path === "/acme/oauth/token")
        throw new Error("Token request probe; no provider response.");
      if (path === "/acme/oauth/userinfo")
        return json({ sub: "01ARZ3NDEKTSV4RRFFQ6000011", email: "ada@acme.test" });
      if (path === "/acme/oauth/revoke")
        return new Response(null, { status: 200 });
      if (path === "/acme/.well-known/openid-configuration")
        return json(discoveryDoc);
      if (path === "/acme/.well-known/jwks.json") {
        return json({
          keys: [
            {
              kty: "EC",
              crv: "P-256",
              alg: "ES256",
              use: "sig",
              kid: "k1",
              x: "eX",
              y: "eY",
            },
          ],
        });
      }
      return json({});
    }, requests);

    const token = await tsAuth.oidc.token("01ARZ3NDEKTSV4RRFFQ6000002", "cs_1", {
      grant_type: "authorization_code",
      code: "c1",
      redirect_uri: "https://app.acme.test/cb",
      code_verifier: "v1",
    });
    assert.equal(token.ok, false);
    assert.equal(
      new URL(/** @type {Request} */ (requests[0]).url).pathname,
      "/acme/oauth/token",
    );
    assert.equal(
      /** @type {Request} */ (requests[0]).headers.get("content-type"),
      "application/x-www-form-urlencoded",
    );
    assert.equal(
      /** @type {Request} */ (requests[0]).headers.get("authorization"),
      null,
    );
    assert.equal(
      await /** @type {Request} */ (requests[0]).text(),
      "client_id=01ARZ3NDEKTSV4RRFFQ6000002&client_secret=cs_1&grant_type=authorization_code&code=c1&redirect_uri=https%3A%2F%2Fapp.acme.test%2Fcb&code_verifier=v1",
    );

    const discovery = await tsAuth.oidc.discovery();
    assert.equal(discovery.ok, true);
    if (discovery.ok)
      assert.equal(
        discovery.value.token_endpoint_auth_methods_supported[0],
        "client_secret_post",
      );
    assert.equal(
      new URL(/** @type {Request} */ (requests[1]).url).pathname,
      "/acme/.well-known/openid-configuration",
    );
    assert.equal(
      /** @type {Request} */ (requests[1]).headers.get("authorization"),
      null,
    );

    const jwks = await tsAuth.oidc.jwks();
    assert.equal(jwks.ok, true);
    assert.equal(
      new URL(/** @type {Request} */ (requests[2]).url).pathname,
      "/acme/.well-known/jwks.json",
    );
    assert.equal(
      /** @type {Request} */ (requests[2]).headers.get("authorization"),
      null,
    );

    const revoked = await tsAuth.oidc.revoke("01ARZ3NDEKTSV4RRFFQ6000002", "cs_1", "at_1");
    assert.equal(revoked.ok, true);
    assert.equal(
      new URL(/** @type {Request} */ (requests[3]).url).pathname,
      "/acme/oauth/revoke",
    );
    assert.equal(
      /** @type {Request} */ (requests[3]).headers.get("content-type"),
      "application/x-www-form-urlencoded",
    );
    assert.equal(
      await /** @type {Request} */ (requests[3]).text(),
      "client_id=01ARZ3NDEKTSV4RRFFQ6000002&client_secret=cs_1&token=at_1",
    );

    const userinfo = await tsAuth.oidc.userinfo("at_1");
    assert.equal(userinfo.ok, true);
    if (userinfo.ok) assert.equal(userinfo.value.sub, "01ARZ3NDEKTSV4RRFFQ6000011");
    assert.equal(
      new URL(/** @type {Request} */ (requests[4]).url).pathname,
      "/acme/oauth/userinfo",
    );
    assert.equal(
      /** @type {Request} */ (requests[4]).headers.get("authorization"),
      "Bearer at_1",
    );
  });

  test("refreshes a public client without a client secret", async () => {
    /** @type {Request[]} */
    const requests = [];
    const tsAuth = tsAuthClient((_request, index) => {
      if (index === 0)
        return json({
          access_token: "at_2",
          token_type: "Bearer",
          expires_in: 300,
          scope: "openid email offline_access",
          refresh_token: "rt_2",
        });
      return json({
        access_token: "at_3",
        token_type: "Bearer",
        expires_in: 300,
        scope: "openid email offline_access",
        refresh_token: "rt_3",
      });
    }, requests);

    const first = await tsAuth.oidc.refreshPublic(
      "01ARZ3NDEKTSV4RRFFQ6000002",
      "rt_1",
    );
    assert.equal(first.ok, true);
    if (first.ok) assert.equal(first.value.refresh_token, "rt_2");
    assert.equal(
      new URL(/** @type {Request} */ (requests[0]).url).pathname,
      "/acme/oauth/token",
    );
    assert.equal(
      /** @type {Request} */ (requests[0]).headers.get("content-type"),
      "application/x-www-form-urlencoded",
    );
    assert.equal(
      /** @type {Request} */ (requests[0]).headers.get("authorization"),
      null,
    );
    const firstForm = new URLSearchParams(
      await /** @type {Request} */ (requests[0]).text(),
    );
    assert.deepEqual([...firstForm], [
      ["client_id", "01ARZ3NDEKTSV4RRFFQ6000002"],
      ["grant_type", "refresh_token"],
      ["refresh_token", "rt_1"],
    ]);

    const second = await tsAuth.oidc.refreshPublic(
      "01ARZ3NDEKTSV4RRFFQ6000002",
      "rt_2",
    );
    assert.equal(second.ok, true);
    if (second.ok) assert.equal(second.value.refresh_token, "rt_3");
    const secondForm = new URLSearchParams(
      await /** @type {Request} */ (requests[1]).text(),
    );
    assert.equal(secondForm.get("client_secret"), null);
    assert.equal(secondForm.get("refresh_token"), "rt_2");

    assert.throws(
      () => tsAuth.oidc.refreshPublic("", "rt_1"),
      /clientId/u,
    );
    assert.throws(
      () => tsAuth.oidc.refreshPublic("01ARZ3NDEKTSV4RRFFQ6000002", "  "),
      /refreshToken/u,
    );
    assert.throws(
      () => tsAuth.oidc.refreshPublic("01ARZ3NDEKTSV4RRFFQ6000002", "rt\u00001"),
      /refreshToken/u,
    );
    assert.equal(requests.length, 2);
  });
});

// These controls inspect SDK-owned requests with an always-throwing transport;
// no successful hosted identity service response is fabricated.

// This is producer-byte playback for schema interoperability, not a live or
// authenticated hosted boundary. Without retained producer artifacts it skips.

// These are unchanged bytes captured from the accepted real Auth handlers.
// Playback establishes decoding interoperability only, without hosted access.
