/* Edge cache keys. A key holds the path, the fragment flag and only the
   query parameters a route declares, each checked against its allowed
   values, so arbitrary query strings can never bypass the cache. */

/** Allowed query parameters of a route and the check each value must pass. */
export type QuerySpec = Readonly<Record<string, (value: string) => boolean>>;

/** Accepts one of a fixed set of values. */
export const oneOf =
  (...values: readonly string[]) =>
  (value: string): boolean =>
    values.includes(value);

/** Accepts short free text, such as a search. */
export const text =
  (max: number) =>
  (value: string): boolean =>
    value.length > 0 && value.length <= max;

/** The route's valid query parameters present in the URL. */
export function readQuery(url: URL, spec: QuerySpec = {}): Record<string, string> {
  const query: Record<string, string> = {};
  for (const [name, valid] of Object.entries(spec)) {
    const value = url.searchParams.get(name)?.trim();
    if (value && valid(value)) query[name] = value;
  }
  return query;
}

/** The cache key of a request: origin, path, then parameters sorted by name. */
export function cacheKey(url: URL, query: Readonly<Record<string, string>>, fragment: boolean): string {
  const params = new URLSearchParams(Object.entries(query).sort(([a], [b]) => a.localeCompare(b)));
  if (fragment) params.set("fragment", "main");
  const search = params.toString();
  return `${url.origin}${url.pathname}${search ? `?${search}` : ""}`;
}
