import type { TerraScaleSqliteStore } from "./store.js";

export type LocalJsonObject = Record<string, unknown>;

export type LocalAuthenticationMode = "permissive" | "required";

export type LocalPrincipal = {
  readonly userId: string;
};

export type LocalDatabaseIdentity = {
  readonly databaseId: string;
  readonly name: string;
  readonly projectId: string;
};

export type LocalRequestContext = {
  readonly authentication: LocalAuthenticationMode;
  readonly body: unknown;
  readonly method: string;
  readonly now: string;
  readonly pathname: string;
  readonly principal: LocalPrincipal | undefined;
  readonly resolvedDatabase: LocalDatabaseIdentity | undefined;
  readonly request: Request;
  readonly store: TerraScaleSqliteStore;
  readonly url: URL;
};

export type LocalHandler = (
  context: LocalRequestContext,
) => Promise<Response | undefined> | Response | undefined;

export function matchLocalRoute(
  context: LocalRequestContext,
  method: string,
  template: string,
): Readonly<Record<string, string>> | undefined {
  if (context.method !== method) {
    return undefined;
  }

  const names: string[] = [];
  const pattern = template
    .split("/")
    .map((segment) => {
      const matches = [...segment.matchAll(/\{([A-Za-z][A-Za-z0-9]*)\}/gu)];
      if (matches.length === 0) {
        return escapePattern(segment);
      }

      let source = "";
      let offset = 0;
      for (const match of matches) {
        const index = match.index;
        const name = match[1];
        if (index === undefined || name === undefined) {
          continue;
        }
        source += escapePattern(segment.slice(offset, index));
        source += "([^/]+)";
        names.push(name);
        offset = index + match[0].length;
      }
      return `${source}${escapePattern(segment.slice(offset))}`;
    })
    .join("/");
  const match = new RegExp(`^${pattern}$`, "u").exec(context.pathname);
  if (match === null) {
    return undefined;
  }

  return Object.fromEntries(
    names.map((name, index) => [name, decodeURIComponent(match[index + 1] ?? "")]),
  );
}

export function jsonResponse(
  value: unknown,
  status = 200,
  headers?: HeadersInit,
): Response {
  const responseHeaders = new Headers(headers);
  responseHeaders.set("content-type", "application/json; charset=utf-8");
  responseHeaders.set("x-terrascale-edge-region", "local");
  responseHeaders.set("x-terrascale-serving-region", "local");
  responseHeaders.set("x-terrascale-route-mode", "global");
  return new Response(JSON.stringify(value), { status, headers: responseHeaders });
}

export function emptyResponse(status = 204, headers?: HeadersInit): Response {
  const responseHeaders = new Headers(headers);
  responseHeaders.set("x-terrascale-edge-region", "local");
  responseHeaders.set("x-terrascale-serving-region", "local");
  responseHeaders.set("x-terrascale-route-mode", "global");
  return new Response(null, { status, headers: responseHeaders });
}

export function problemResponse(
  status: number,
  code: string,
  message: string,
  details?: LocalJsonObject,
): Response {
  return jsonResponse(
    {
      error: {
        code,
        message,
        ...(details ?? {}),
      },
    },
    status,
  );
}

export function objectBody(context: LocalRequestContext): LocalJsonObject {
  if (context.body === null || typeof context.body !== "object" || Array.isArray(context.body)) {
    return {};
  }
  return context.body as LocalJsonObject;
}

export function page<T>(items: readonly T[]): {
  readonly items: readonly T[];
  readonly nextCursor: null;
  readonly total: number;
} {
  return { items, nextCursor: null, total: items.length };
}

function escapePattern(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&");
}
