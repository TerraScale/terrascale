/* Webhook targets. Discord and Slack URLs are recognized by their exact
   shape; any other HTTPS URL is a generic webhook. Targets that could reach
   internal hosts (IP literals, localhost, TerraScale's own domain,
   credentials, odd ports) are refused. */

export type Flavor = "discord" | "slack" | "generic";

const DISCORD_HOSTS = ["discord.com", "discordapp.com", "ptb.discord.com", "canary.discord.com"];

/** The webhook's flavor, or null when the URL is not an acceptable target. */
export function webhookFlavor(text: string): Flavor | null {
  if (text.length > 500) return null;
  let url: URL;
  try {
    url = new URL(text);
  } catch {
    return null;
  }
  if (url.protocol !== "https:" || url.username || url.password || url.port) return null;
  // "localhost." and "localhost" are the same host.
  const host = url.hostname.toLowerCase().replace(/\.+$/, "");
  if (
    host === "localhost" ||
    host.endsWith(".localhost") ||
    host.endsWith(".internal") ||
    host.endsWith(".local") ||
    host === "terrascale.tech" ||
    host.endsWith(".terrascale.tech") ||
    /^[\d.]+$/.test(host) ||
    host.startsWith("[") ||
    !host.includes(".")
  ) {
    return null;
  }
  if (DISCORD_HOSTS.includes(host)) return /^\/api\/webhooks\/\d+\/[\w-]+\/?$/.test(url.pathname) ? "discord" : null;
  if (host === "hooks.slack.com") return /^\/services\/[\w/]+$/.test(url.pathname) ? "slack" : null;
  return "generic";
}

/** A plausible email address, short enough for SMTP. */
export const isEmail = (text: string): boolean =>
  text.length <= 254 && /^[^\s@<>()",;:\\[\]]+@[a-z0-9.-]+\.[a-z]{2,}$/i.test(text);

export async function hmacHex(secret: string, body: string): Promise<string> {
  const key = await crypto.subtle.importKey(
    "raw",
    new TextEncoder().encode(secret),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"],
  );
  const signature = new Uint8Array(await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(body)));
  return [...signature].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

/** Posts a payload; redirects are not followed, so a target cannot bounce
    the request elsewhere. */
export async function post(
  url: string,
  body: string,
  headers: Record<string, string>,
  fetcher: typeof fetch = fetch,
): Promise<void> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 10_000);
  try {
    const response = await fetcher(url, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "user-agent": "TerraScale-Status/2 (+https://status.terrascale.tech)",
        ...headers,
      },
      body,
      redirect: "manual",
      signal: controller.signal,
    });
    await response.body?.cancel();
    if (!response.ok) throw new Error(`Webhook HTTP ${response.status}`);
  } finally {
    clearTimeout(timer);
  }
}
