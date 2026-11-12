export const apiService = Object.freeze({ host: "api.terrascale.tech", name: "the TerraBase API" });
export const authService = Object.freeze({ host: "auth.terrascale.tech", name: "ts-auth" });

/** @param {string} text */
const digits = (text) => /^[0-9]+$/u.test(text);

/** @param {string} host */
function ipv4(host) {
  const octets = host.split(".");
  return octets.length === 4 && octets.every((octet) => digits(octet) && octet.length <= 3 && !(octet.length > 1 && octet[0] === "0") && Number(octet) <= 255);
}

/** @param {string} text @returns {number[] | null} */
function ipv6(text) {
  const halves = text.split("::");
  if (text === "" || halves.length > 2) return null;
  const parts = halves.map((half) => (half === "" ? [] : half.split(":")));
  const count = parts.reduce((sum, half) => sum + half.length, 0);
  if ((parts.length === 1 && count !== 8) || (parts.length === 2 && count > 7)) return null;
  if (!parts.flat().every((group) => /^[0-9a-f]{1,4}$/u.test(group))) return null;
  const [head = [], tail = []] = parts;
  return [...head, ...Array(8 - count).fill("0"), ...tail].map((group) => parseInt(group, 16));
}

/** @param {string} host */
function name(host) {
  const labels = host.split(".");
  if (!labels.every((label) => /^[a-z0-9-]+$/u.test(label))) return false;
  const last = labels[labels.length - 1] ?? "";
  return digits(last) || /^0x[0-9a-f]*$/u.test(last) ? ipv4(host) : true;
}

/** @param {string} host */
export function isLoopbackHost(host = "") {
  const lower = host.toLowerCase();
  if (lower === "localhost" || (lower.startsWith("127.") && ipv4(lower))) return true;
  const groups = lower.startsWith("[") && lower.endsWith("]") ? ipv6(lower.slice(1, -1)) : null;
  return groups !== null && groups.every((group, index) => group === (index === 7 ? 1 : 0));
}

/** @param {unknown} raw @returns {{ scheme: string, host: string, port: string } | null} */
function strictOrigin(raw) {
  if (typeof raw !== "string" || !/^[0-9A-Za-z.:[\]/-]*$/u.test(raw)) return null;
  const lower = raw.toLowerCase();
  const split = lower.indexOf("://");
  if (split < 0) return null;
  const scheme = lower.slice(0, split);
  const rest = lower.slice(split + 3).replace(/\/$/u, "");
  if ((scheme !== "https" && scheme !== "http") || rest.includes("/")) return null;
  let host = rest;
  let after = "";
  if (rest.startsWith("[")) {
    const end = rest.indexOf("]");
    if (end < 0 || ipv6(rest.slice(1, end)) === null) return null;
    host = rest.slice(0, end + 1);
    after = rest.slice(end + 1);
  } else if (rest.includes(":")) {
    host = rest.slice(0, rest.indexOf(":"));
    after = rest.slice(rest.indexOf(":"));
  }
  if (!host.startsWith("[") && !name(host)) return null;
  if (after === "") return { scheme, host, port: "" };
  const port = after.slice(1);
  if (after[0] !== ":" || !digits(port) || port.length > 5 || port[0] === "0" || Number(port) > 65535) return null;
  return { scheme, host, port: (scheme === "https" && port === "443") || (scheme === "http" && port === "80") ? "" : port };
}

/** @param {{ host: string }} service @param {string} host */
function serves(service, host) {
  if (host !== "terrascale.tech" && !host.endsWith(".terrascale.tech")) return true;
  const label = host.slice(0, -(service.host.length + 1));
  return host === service.host || (host.endsWith(`.${service.host}`) && label !== "" && !label.includes("."));
}

/** @param {string} a @param {string} b */
function sameHost(a, b) {
  if (!a.startsWith("[")) return a === b;
  const left = ipv6(a.slice(1, -1));
  const right = ipv6(b.slice(1, -1));
  return left !== null && right !== null && left.every((group, index) => group === right[index]);
}

/**
 * @param {unknown} raw
 * @param {string} what
 * @param {{ host: string, name: string }} service
 * @returns {string}
 */
export function serviceOrigin(raw, what, service) {
  const checked = strictOrigin(raw);
  if (checked === null) throw new TypeError(`${what} must be exactly scheme://host[:port], such as https://${service.host}.`);
  const { scheme, host, port } = checked;
  if (scheme === "http" && !isLoopbackHost(host))
    throw new TypeError(`${what} must use https; plain http is allowed only for localhost, 127.0.0.0/8 and [::1].`);
  if (!serves(service, host)) throw new TypeError(`${what} names a TerraScale host that does not serve ${service.name}.`);
  const origin = `${scheme}://${host}${port === "" ? "" : `:${port}`}`;
  const parsed = URL.canParse(origin) ? new URL(origin) : null;
  if (parsed === null || !sameHost(parsed.hostname, host) || parsed.port !== port)
    throw new TypeError(`${what} is read as another host or port by the HTTP client.`);
  return parsed.origin;
}
