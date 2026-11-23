/* A small path router: "/incidents/:id" patterns compiled to expressions.
   Parameters match one path segment and arrive decoded. */

export interface Match<T> {
  value: T;
  params: Record<string, string>;
}

export type Router<T> = (path: string) => Match<T> | null;

const escape = (text: string): string => text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

/** "/badge/:id.svg" → /^\/badge\/(?<id>[^/]+?)\.svg$/ */
function compile(pattern: string): RegExp {
  let source = "";
  let last = 0;
  for (const match of pattern.matchAll(/:([A-Za-z]\w*)/g)) {
    source += `${escape(pattern.slice(last, match.index))}(?<${match[1]}>[^/]+?)`;
    last = match.index + match[0].length;
  }
  return new RegExp(`^${source}${escape(pattern.slice(last))}$`);
}

/** Routes are tried in the order given; the first match wins. */
export function createRouter<T>(routes: Record<string, T>): Router<T> {
  const compiled = Object.entries(routes).map(([pattern, value]) => ({ pattern: compile(pattern), value }));
  return (path) => {
    for (const { pattern, value } of compiled) {
      const match = pattern.exec(path);
      if (!match) continue;
      const params: Record<string, string> = {};
      try {
        for (const [name, raw] of Object.entries(match.groups ?? {})) params[name] = decodeURIComponent(raw);
      } catch {
        return null;
      }
      return { value, params };
    }
    return null;
  };
}
