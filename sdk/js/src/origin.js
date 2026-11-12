export const apiService = Object.freeze({ host: "api.terrascale.tech", regional: true, name: "the TerraBase API" });
export const authService = Object.freeze({ host: "auth.terrascale.tech", regional: true, name: "ts-auth" });

export function isLoopbackHost(hostname = "") {
  const octets = /^127\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/u.exec(hostname);
  return hostname === "localhost" || hostname === "[::1]" || (octets !== null && octets.slice(1).every((octet) => Number(octet) <= 255));
}

function serves(service = { host: "", regional: false, name: "" }, hostname = "") {
  if (hostname !== "terrascale.tech" && !hostname.endsWith(".terrascale.tech")) return true;
  if (hostname === service.host) return true;
  const label = hostname.slice(0, -(service.host.length + 1));
  return service.regional && hostname.endsWith(`.${service.host}`) && label !== "" && !label.includes(".");
}

export function serviceOrigin(raw = "", what = "", service = { host: "", regional: false, name: "" }) {
  let url;
  try {
    url = new URL(raw);
  } catch {
    throw new TypeError(`${what} must be an absolute https URL.`);
  }
  if (url.protocol !== "https:" && !(url.protocol === "http:" && isLoopbackHost(url.hostname)))
    throw new TypeError(`${what} must use https; plain http is allowed only for localhost, 127.0.0.0/8 and [::1].`);
  if (url.username !== "" || url.password !== "") throw new TypeError(`${what} must not carry user info.`);
  if (url.search !== "" || url.hash !== "" || /[?#]/u.test(raw)) throw new TypeError(`${what} must not carry a query or a fragment.`);
  if (url.pathname !== "/") throw new TypeError(`${what} must be an origin without a path.`);
  if (!serves(service, url.hostname)) throw new TypeError(`${what} names a TerraScale host that does not serve ${service.name}.`);
  return url.origin;
}
