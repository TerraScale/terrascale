import { createTerraBaseV1Client } from "@terrascale/sdk";

// Server-only: the API key never reaches the browser. Never prefix it with NEXT_PUBLIC_.
const tokenUrl = "https://auth.terrascale.tech/terrascale/oauth/token";
let cached: { token: string; renewAt: number } | undefined;

function required(name: string): string {
  const value = process.env[name]?.trim();
  if (!value) throw new Error(`Set ${name} in .env.local and restart the dev server.`);
  return value;
}

// Exchanges the project API key for a short-lived access token (client credentials grant).
async function accessToken(): Promise<string> {
  if (cached && Date.now() < cached.renewAt) return cached.token;
  const key = required("TERRASCALE_API_KEY");
  const response = await fetch(tokenUrl, {
    method: "POST",
    headers: {
      authorization: `Basic ${Buffer.from(`${key.split("_")[0]}:${key}`).toString("base64")}`,
      "content-type": "application/x-www-form-urlencoded",
    },
    body: new URLSearchParams({ grant_type: "client_credentials" }),
    cache: "no-store",
  });
  const body = await response.json().catch(() => ({})) as { access_token?: unknown; expires_in?: unknown };
  if (!response.ok || typeof body.access_token !== "string" || typeof body.expires_in !== "number") {
    throw new Error(`API key exchange failed (${response.status}). Check TERRASCALE_API_KEY.`);
  }
  // Renew 60 s before expiry, or halfway through a shorter lifetime (API key tokens last 60 s).
  const renewIn = body.expires_in - Math.min(60, body.expires_in / 2);
  cached = { token: body.access_token, renewAt: Date.now() + Math.max(0, renewIn) * 1000 };
  return cached.token;
}

export async function database() {
  const client = createTerraBaseV1Client({
    baseUrl: "https://api.terrascale.tech",
    project: required("TERRASCALE_PROJECT_ID"),
    token: await accessToken(),
  });
  const db = client.database(required("TERRASCALE_DATABASE"));
  // IF NOT EXISTS keeps this safe on every request.
  await db.execute("CREATE TABLE IF NOT EXISTS posts (id TEXT PRIMARY KEY, title TEXT NOT NULL, published_at TEXT NOT NULL)");
  return db;
}
