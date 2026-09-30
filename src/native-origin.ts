/** Internal admission policy shared by native clients and qualification configuration. */
export const terraScaleServiceDomain = "terrascale.tech";

const reservedRegions = new Set([
  "API", "ATLAS", "AUTH", "SYNC", "STATUS", "WWW", "APP", "DASH",
  "DOCS", "GIT", "CDN", "ADMIN", "MCP", "MAIL",
]);

function isNativeRegion(value: unknown): value is string {
  return typeof value === "string" && /^[A-Z0-9][A-Z0-9-]{0,15}$/u.test(value) && !reservedRegions.has(value);
}

export function requireNativeRegion(value: unknown): string {
  if (!isNativeRegion(value)) throw new TypeError("Invalid native region.");
  return value;
}

export function nativeRegionalOrigin(code: unknown): string {
  return `https://${requireNativeRegion(code).toLowerCase()}.${terraScaleServiceDomain}`;
}

/** Hosted identity also refuses the obsolete test API zone, without using native admission. */
export function isTestApiHostname(hostname: string): boolean {
  const canonical = hostname.toLowerCase().replace(/\.+$/u, "");
  const testDomain = `api.${terraScaleServiceDomain}`;
  return canonical === testDomain || canonical.endsWith(`.${testDomain}`);
}

export function nativeServiceOrigin(raw: string): string {
  const url = new URL(raw);
  if (url.username !== "" || url.password !== "" || url.search !== "" || url.hash !== "" || url.pathname !== "/") {
    throw new TypeError("Service origin must be bare and contain no credentials.");
  }
  if (isTestApiHostname(url.hostname)) {
    throw new TypeError("The test API host is not a regional TerraBase endpoint.");
  }
  const hostname = url.hostname.replace(/\.+$/u, "");
  const regionalSuffix = `.${terraScaleServiceDomain}`;
  if (hostname === terraScaleServiceDomain || hostname.endsWith(regionalSuffix)) {
    const code = hostname.slice(0, -regionalSuffix.length).toUpperCase();
    const authority = raw.slice(raw.indexOf("//") + 2).split(/[/?#]/u, 1)[0]!;
    if (!isNativeRegion(code) || url.origin !== nativeRegionalOrigin(code) || url.hostname !== hostname || authority !== hostname) {
      throw new TypeError("TerraScale service origins must use a canonical regional hostname.");
    }
  }
  if (url.hostname !== hostname) throw new TypeError("Service origins must use a canonical hostname.");
  const loopback = ["localhost", "127.0.0.1", "[::1]"].includes(hostname) || hostname.endsWith(".localhost");
  if (url.protocol !== "https:" && !(url.protocol === "http:" && loopback)) {
    throw new TypeError("Service origins must use HTTPS, except loopback development.");
  }
  return url.origin;
}
