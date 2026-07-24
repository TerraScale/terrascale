import assert from "node:assert/strict";
import { test } from "node:test";

import { createTerraScaleClient } from "../factory.js";

test("public external-login contracts carry the supported Apple provider", async () => {
  const requests: Request[] = [];
  const client = createTerraScaleClient({
    apiBaseUrl: "https://api.terrascale.test",
    authBaseUrl: "https://auth.terrascale.test",
    fetch: async (input) => {
      const request = input instanceof Request ? input : new Request(input);
      requests.push(request);
      return Response.json(
        request.url.endsWith("/begin")
          ? { authorizeUrl: "https://appleid.apple.com/auth/authorize" }
          : { accessToken: "access", refreshToken: "refresh", expiresIn: 3600 },
      );
    },
  });

  await client.auth.beginExternalLogin({
    provider: "apple",
    redirectUri: "https://dashboard.terrascale.test/auth/callback",
    state: "state",
  });
  await client.auth.completeExternalLogin({
    provider: "apple",
    code: "authorization-code",
    idToken: "identity-token",
    redirectUri: "https://dashboard.terrascale.test/auth/callback",
  });

  assert.equal(requests.length, 2);
  assert.equal(await requests[0]?.clone().json().then((body) => body.provider), "apple");
  assert.equal(await requests[1]?.clone().json().then((body) => body.provider), "apple");
});
