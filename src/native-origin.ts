/** Internal admission policy shared by native clients and qualification configuration. */
export const terraScaleServiceDomain = "terrascale.tech";
const dataApiDomain = `api.${terraScaleServiceDomain}`;

const reservedRegions = new Set([
  "API", "ATLAS", "AUTH", "SYNC", "STATUS", "WWW", "APP", "DASH",
  "DOCS", "GIT", "CDN", "ADMIN", "MCP", "MAIL",
]);

function isNativeRegion(value: unknown): value is string {
  return typeof value === "string" && /^[A-Z0-9][A-Z0-9-]{0,15}$/u.test(value) && !value.endsWith("-") && !value.includes("--") && !reservedRegions.has(value);
}

export function requireNativeRegion(value: unknown): string {
  if (!isNativeRegion(value)) throw new TypeError("Invalid native region.");
  return value;
}

export function nativeRegionalOrigin(code: unknown): string {
  return `https://${requireNativeRegion(code).toLowerCase()}.${dataApiDomain}`;
}

/** Hosted identity refuses database API origins, without using native admission. */
export function isDataApiHostname(hostname: string): boolean {
  const canonical = hostname.toLowerCase().replace(/\.+$/u, "");
  return canonical === dataApiDomain || canonical.endsWith(`.${dataApiDomain}`);
}

export function nativeServiceOrigin(raw: string): string {
  const url = new URL(raw);
  if (url.username !== "" || url.password !== "" || url.search !== "" || url.hash !== "" || url.pathname !== "/") {
    throw new TypeError("Service origin must be bare and contain no credentials.");
  }
  const authority = raw.slice(raw.indexOf("//") + 2).split(/[/?#]/u, 1)[0]!;
  const rawHostname = authority.startsWith("[") ? authority.slice(0, authority.indexOf("]") + 1) : authority.split(":", 1)[0]!;
  // Independent origins may use ASCII case aliases, but must not be silently
  // redirected through percent decoding or IDNA hostname normalization.
  if (!/^[\x00-\x7f]+$/u.test(rawHostname) || rawHostname.toLowerCase() !== url.hostname) {
    throw new TypeError("Service origins must use a canonical ASCII hostname.");
  }
  const hostname = url.hostname.replace(/\.+$/u, "");
  const regionalSuffix = `.${terraScaleServiceDomain}`;
  if (hostname === terraScaleServiceDomain || hostname.endsWith(regionalSuffix)) {
    const code = hostname.endsWith(`.${dataApiDomain}`) ? hostname.slice(0, -(dataApiDomain.length + 1)).toUpperCase() : "";
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
