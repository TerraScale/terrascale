/** Internal admission policy shared by native clients and qualification configuration. */
export const terraScaleServiceDomain = "terrascale.tech";
const dataApiDomain = `api.${terraScaleServiceDomain}`;

const reservedRegions = new Set([
  "API", "ATLAS", "AUTH", "SYNC", "STATUS", "WWW", "APP", "DASH",
  "DOCS", "GIT", "CDN", "ADMIN", "MCP", "MAIL",
]);

/**
 * @param {unknown} value
 * @returns {value is string}
 */
function isNativeRegion(value) {
  return typeof value === "string" && /^[A-Z0-9][A-Z0-9-]{0,15}$/u.test(value) && !value.endsWith("-") && !value.includes("--") && !reservedRegions.has(value);
}

/**
 * @param {unknown} value
 * @returns {string}
 */
export function requireNativeRegion(value) {
  if (!isNativeRegion(value)) throw new TypeError("Invalid native region.");
  return value;
}

/**
 * @param {unknown} code
 * @returns {string}
 */
export function nativeRegionalOrigin(code) {
  return `https://${requireNativeRegion(code).toLowerCase()}.${dataApiDomain}`;
}

/**
 * Hosted identity refuses database API origins, without using native admission.
 * @param {string} hostname
 * @returns {boolean}
 */
export function isDataApiHostname(hostname) {
  const canonical = hostname.toLowerCase().replace(/\.+$/u, "");
  return canonical === dataApiDomain || canonical.endsWith(`.${dataApiDomain}`);
}

/**
 * @param {string} raw
 * @returns {string}
 */
export function nativeServiceOrigin(raw) {
  const url = new URL(raw);
  if (url.username !== "" || url.password !== "" || url.search !== "" || url.hash !== "" || url.pathname !== "/") {
    throw new TypeError("Service origin must be bare and contain no credentials.");
  }
  const authority = /** @type {string} */ (raw.slice(raw.indexOf("//") + 2).split(/[/?#]/u, 1)[0]);
  const rawHostname = authority.startsWith("[") ? authority.slice(0, authority.indexOf("]") + 1) : /** @type {string} */ (authority.split(":", 1)[0]);
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
