export const oauthClientId = "terrascale-starter-web";
export type Credential = { accessToken: () => Promise<string>; disconnect: () => void };
export type OAuthTokens = { accessToken: string; refreshToken: string; renewAt: number };
type AuthConfiguration = { authBaseUrl: string; apiBaseUrl: string; projectId: string; databaseId: string };

export async function readOAuthTokens(response: Response): Promise<OAuthTokens> {
  const body = await response.json() as {
    access_token?: unknown; refresh_token?: unknown; expires_in?: unknown; error_description?: unknown;
  };
  if (!response.ok || typeof body.access_token !== "string" || !body.access_token ||
      typeof body.refresh_token !== "string" || !body.refresh_token ||
      typeof body.expires_in !== "number" || !Number.isFinite(body.expires_in) || body.expires_in <= 0) {
    throw new Error(typeof body.error_description === "string" ? body.error_description : "Sign-in could not be renewed. Disconnect and sign in again.");
  }
  return {
    accessToken: body.access_token,
    refreshToken: body.refresh_token,
    renewAt: Date.now() + Math.max(0, body.expires_in - 10) * 1000,
  };
}

// OAuth refresh rotation and delegation are serialized together. Neither token
// leaves tab memory, and disconnect cancels an in-flight exchange and clears both.
export function createStarterCredential(initialTokens: OAuthTokens, config: AuthConfiguration): Credential {
  const { authBaseUrl, apiBaseUrl, projectId, databaseId } = config;
  let tokens: OAuthTokens | undefined = initialTokens;
  let cached: { token: string; renewAt: number } | undefined;
  let pending: Promise<string> | undefined;
  const controller = new AbortController();
  return {
    disconnect() {
      controller.abort();
      tokens = undefined;
      cached = undefined;
    },
    async accessToken() {
      if (controller.signal.aborted) throw new Error("Disconnected. Sign in again.");
      if (cached && Date.now() < cached.renewAt) return cached.token;
      if (pending) return pending;
      pending = (async () => {
        if (!tokens) throw new Error("Sign-in expired. Disconnect and sign in again.");
        if (Date.now() >= tokens.renewAt) {
          const refreshToken = tokens.refreshToken;
          // A lost refresh response leaves rotation uncertain: never replay it.
          tokens = undefined;
          const response = await fetch(new URL("/api/auth/oauth2/token", authBaseUrl), {
            method: "POST",
            signal: controller.signal,
            headers: { "content-type": "application/x-www-form-urlencoded" },
            body: new URLSearchParams({ grant_type: "refresh_token", client_id: oauthClientId, refresh_token: refreshToken, resource: apiBaseUrl }),
          });
          const refreshed = await readOAuthTokens(response);
          if (controller.signal.aborted) throw new Error("Disconnected. Sign in again.");
          tokens = refreshed;
        }
        const response = await fetch(new URL("/api/auth/web-starter/delegation", authBaseUrl), {
          method: "POST",
          signal: controller.signal,
          headers: { "content-type": "application/json", authorization: `Bearer ${tokens.accessToken}` },
          body: JSON.stringify({ projectId, databaseId, collection: "posts", actions: ["read", "write"] }),
        });
        const body = await response.json() as { accessToken?: unknown; expiresIn?: unknown; message?: unknown };
        if (!response.ok || typeof body.accessToken !== "string" || !body.accessToken ||
            typeof body.expiresIn !== "number" || !Number.isFinite(body.expiresIn) || body.expiresIn <= 0) {
          throw new Error(typeof body.message === "string" ? body.message : "Database authorization failed. Verify access to this project and branch, or disconnect and sign in again.");
        }
        if (controller.signal.aborted) throw new Error("Disconnected. Sign in again.");
        cached = { token: body.accessToken, renewAt: Date.now() + Math.max(0, body.expiresIn - 10) * 1000 };
        return cached.token;
      })();
      try { return await pending; }
      finally { pending = undefined; }
    },
  };
}
