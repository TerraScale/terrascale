import { expect, test } from "vitest";
import { createTsAuthClient } from "../src/ts-auth.js";

test("named OIDC errors preserve RFC6749 codes and safe response diagnostics", async () => {
  const client = createTsAuthClient({
    apiBaseUrl: "https://auth.example.test",
    instanceSlug: "example",
    fetch: async () =>
      Response.json(
        {
          error: "invalid_client",
          error_description: "Client authentication failed.",
          category: "Authentication",
          traceId: "safe-trace",
          access_token: "oidc-diagnostic-canary",
        },
        { status: 401, headers: { "x-request-id": "safe-request" } },
      ),
  });
  const result = await client.oidc.token("01ARZ3NDEKTSV4RRFFQ69G5FAV", "secret", {
    grant_type: "client_credentials",
    resource: "https://api.example.test/management",
    scope: "management:read",
  });
  expect(result.ok).toBe(false);
  if (!result.ok) {
    expect(result.error.code).toBe("invalid_client");
    expect(result.error.kind).toBe("authentication");
    expect(result.status).toBe(401);
    expect(result.error.status).toBe(401);
    expect(result.error.category).toBe("Authentication");
    expect(result.error.traceId).toBe("safe-trace");
    expect(result.error.requestId).toBe("safe-request");
    expect(result.headers?.get("x-request-id")).toBe("safe-request");
    expect(result.body).toMatchObject({ access_token: "[redacted]" });
    expect(JSON.stringify(result)).not.toContain("oidc-diagnostic-canary");
  }
});

test("malformed and non-JSON webhook responses omit secret-bearing diagnostics", async () => {
  const secret = "whsec_quoted_response_canary";
  for (const scenario of [
    {
      status: 201,
      contentType: "application/json",
      body: `{"signing_secret":"${secret}",`,
    },
    { status: 201, contentType: "application/json", body: secret },
    {
      status: 401,
      contentType: "application/json",
      body: `{"signing_secret":"${secret}",`,
    },
    {
      status: 200,
      contentType: "text/html",
      body: `{"signing_secret":"${secret}"}`,
    },
    {
      status: 503,
      contentType: "text/html",
      body: `{"signing_secret":"${secret}"}`,
    },
  ]) {
    const client = createTsAuthClient({
      apiBaseUrl: "https://auth.example.test",
      instanceSlug: "example",
      fetch: async () =>
        new Response(scenario.body, {
          status: scenario.status,
          headers: {
            "content-type": scenario.contentType,
            "x-request-id": "webhook-safe-request",
          },
        }),
    });
    const result = await client.webhooks.create({
      url: "https://hooks.example.test/events",
      events: ["user.*"],
    });
    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.routeName).toBe("tsauth.webhooks.create");
      expect(result.status).toBe(scenario.status);
      expect(result.error.status).toBe(scenario.status);
      expect(result.error.requestId).toBe("webhook-safe-request");
      expect(result.headers?.get("x-request-id")).toBe("webhook-safe-request");
      expect(result.error.kind).toBe(
        scenario.status < 400
          ? "invalid-response"
          : scenario.status === 401
            ? "authentication"
            : "server",
      );
      expect(result.body).toBeUndefined();
      expect(result.error.details).toBeUndefined();
      expect(JSON.stringify(result)).not.toContain(secret);
      if (scenario.status < 400)
        expect(result.error.message).toBe(
          scenario.contentType === "application/json"
            ? "ts-auth returned an invalid JSON response body."
            : "ts-auth returned a non-JSON response body.",
        );
    }
  }
});

test("OAuth schema failures preserve HTTP metadata without retaining secret payloads", async () => {
  const secret = "oauth_schema_response_canary";
  for (const headerName of ["x-request-id", "traceparent"]) {
    const client = createTsAuthClient({
      apiBaseUrl: "https://auth.example.test",
      instanceSlug: "example",
      fetch: async () =>
        Response.json(
          { client: {}, client_secret: secret },
          { status: 200, headers: { [headerName]: "oauth-safe-request" } },
        ),
    });
    const operations = [
      {
        routeName: "tsauth.oauthClients.create",
        invoke: () => client.oauthClients.create({}),
      },
      {
        routeName: "tsauth.oauthClients.update",
        invoke: () => client.oauthClients.update("01ARZ3NDEKTSV4RRFFQ69G5FAV", { rotate_secret: true }),
      },
      {
        routeName: "tsauth.oauthResources.update",
        invoke: () =>
          client.oauthResources.update("01ARZ3NDEKTSV4RRFFQ69G5FAV", { expected_auth_epoch: 1 }),
      },
      {
        routeName: "tsauth.oidc.token",
        invoke: () =>
          client.oidc.token("01ARZ3NDEKTSV4RRFFQ69G5FAV", "secret", {
            grant_type: "client_credentials",
            resource: "https://api.example.test/management",
            scope: "management:read",
          }),
      },
    ];
    for (const operation of operations) {
      const result = await operation.invoke();
      expect(result.ok).toBe(false);
      if (!result.ok) {
        expect(result.routeName).toBe(operation.routeName);
        expect(result.status).toBe(200);
        expect(result.error.status).toBe(200);
        expect(result.error.kind).toBe("invalid-response");
        expect(result.error.code).toBe("invalid_response");
        expect(result.error.requestId).toBe("oauth-safe-request");
        expect(result.headers?.get(headerName)).toBe("oauth-safe-request");
        expect(result.body).toBeUndefined();
        expect(JSON.stringify(result)).not.toContain(secret);
      }
    }
  }
});
