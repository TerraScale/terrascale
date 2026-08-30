import { createHash } from "node:crypto";

import {
  matchesLocalBearerAuthorization,
  normalizeLocalBearerToken,
} from "./authentication.js";
import type { LocalHandler, LocalRequestContext } from "./context.js";
import { emptyResponse, problemResponse } from "./context.js";
import { handleLocalAdmin } from "./handlers/admin.js";
import { handleLocalBackups } from "./handlers/backups.js";
import { handleLocalData } from "./handlers/data.js";
import { handleLocalMetadata } from "./handlers/metadata.js";
import { handleLocalOnboarding } from "./handlers/onboarding.js";
import { handleLocalOrganizations } from "./handlers/organizations.js";
import { handleLocalPlatform } from "./handlers/platform.js";
import { handleLocalQueues } from "./handlers/queues.js";
import { handleLocalSyncV2 } from "./handlers/sync-v2.js";
import { localManifestRoutes } from "./route-manifest.generated.js";
import type { TerraScaleSqliteStore } from "./store.js";

export type LocalAuthenticationMode = "permissive" | "required";

export type TerraScaleLocalRouterOptions = {
  readonly authentication?: LocalAuthenticationMode;
  readonly bearerToken?: string;
};

type ManifestRoute = {
  readonly method: string;
  readonly pathTemplate: string;
  readonly routeName: string;
};

type CompiledManifestRoute = ManifestRoute & {
  readonly names: readonly string[];
  readonly pattern: RegExp;
};

type StoredIdempotencyResponse = {
  readonly body: string;
  readonly fingerprint: string;
  readonly headers: readonly [string, string][];
  readonly status: number;
};

const localHandlers: readonly LocalHandler[] = [
  handleLocalOnboarding,
  handleLocalOrganizations,
  handleLocalMetadata,
  handleLocalQueues,
  handleLocalData,
  handleLocalBackups,
  handleLocalPlatform,
  handleLocalAdmin,
  handleLocalSyncV2,
];

export class TerraScaleLocalRouter {
  readonly #authentication: LocalAuthenticationMode;
  readonly #bearerToken: string | undefined;
  readonly #routes: readonly CompiledManifestRoute[];
  readonly #store: TerraScaleSqliteStore;

  constructor(store: TerraScaleSqliteStore, options: TerraScaleLocalRouterOptions = {}) {
    this.#store = store;
    this.#authentication = options.authentication ?? "permissive";
    if (this.#authentication !== "permissive" && this.#authentication !== "required") {
      throw new TypeError('authentication must be either "permissive" or "required".');
    }
    this.#bearerToken = this.#authentication === "required"
      ? normalizeLocalBearerToken(options.bearerToken)
      : undefined;
    if (this.#authentication === "permissive" && options.bearerToken !== undefined) {
      throw new TypeError('bearerToken can only be set when authentication is "required".');
    }
    this.#routes = loadManifestRoutes();
  }

  readonly fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const request = input instanceof Request ? input : new Request(input, init);
    return this.handle(request);
  };

  async handle(request: Request): Promise<Response> {
    const url = new URL(request.url);
    const method = request.method.toUpperCase();
    const pathname = canonicalPathname(url.pathname);

    if (method === "OPTIONS") {
      return withCors(request, emptyResponse(204));
    }

    if (method === "GET" && (pathname === "/health" || pathname === "/health/ready")) {
      return withCors(request, Response.json({ status: "ready", storage: "sqlite" }));
    }

    if (
      this.#authentication === "required"
      && (this.#bearerToken === undefined
        || !isAuthorized(request, method, pathname, this.#bearerToken))
    ) {
      return withCors(
        request,
        problemResponse(401, "authentication_required", "Supply the configured bearer token."),
      );
    }

    let body: unknown;
    try {
      body = await readBody(request);
    } catch (error) {
      return withCors(
        request,
        problemResponse(
          400,
          "invalid_request_body",
          error instanceof Error ? error.message : "The request body is invalid.",
        ),
      );
    }

    const context: LocalRequestContext = {
      body,
      method,
      now: new Date().toISOString(),
      pathname,
      request,
      store: this.#store,
      url,
    };
    const replay = this.#replayIdempotent(context);
    if (replay !== undefined) {
      return withCors(request, replay);
    }

    let response: Response | undefined;
    try {
      for (const handler of localHandlers) {
        response = await handler(context);
        if (response !== undefined) {
          break;
        }
      }

      if (response === undefined) {
        const match = matchManifestRoute(this.#routes, method, pathname);
        if (match !== undefined) {
          response = problemResponse(
            501,
            "local_operation_unsupported",
            `The local SQLite runtime does not implement ${match.route.method} ${match.route.pathTemplate}.`,
            { route: match.route.routeName },
          );
        }
      }
    } catch (error) {
      response = problemResponse(
        500,
        "local_emulator_error",
        error instanceof Error ? error.message : "The local emulator failed to process the request.",
      );
    }

    response ??= problemResponse(404, "route_not_found", `No public TerraScale route matches ${method} ${pathname}.`);
    response = await this.#rememberIdempotent(context, response);
    return withCors(request, response);
  }

  #replayIdempotent(context: LocalRequestContext): Response | undefined {
    const key = idempotencyKey(context);
    if (key === undefined) {
      return undefined;
    }
    const stored = this.#store.get<StoredIdempotencyResponse>(
      "__idempotency",
      idempotencyScope(context),
      key,
    );
    if (stored === undefined) {
      return undefined;
    }
    const fingerprint = requestFingerprint(context);
    if (stored.fingerprint !== fingerprint) {
      return problemResponse(
        409,
        "idempotency_key_reused",
        "The idempotency key was already used with a different request.",
      );
    }
    const headers = new Headers([...stored.headers]);
    headers.set("idempotency-replayed", "true");
    return new Response(stored.body.length === 0 ? null : stored.body, {
      status: stored.status,
      headers,
    });
  }

  async #rememberIdempotent(
    context: LocalRequestContext,
    response: Response,
  ): Promise<Response> {
    const key = idempotencyKey(context);
    if (key === undefined || !response.ok) {
      return response;
    }
    const clone = response.clone();
    const body = await clone.text();
    this.#store.put<StoredIdempotencyResponse>(
      "__idempotency",
      idempotencyScope(context),
      key,
      {
        body,
        fingerprint: requestFingerprint(context),
        headers: [...clone.headers],
        status: clone.status,
      },
    );
    return response;
  }
}

function canonicalPathname(pathname: string): string {
  return pathname.length > 1 ? pathname.replace(/\/+$/u, "") : pathname;
}

function loadManifestRoutes(): readonly CompiledManifestRoute[] {
  return localManifestRoutes.map((route) => {
    const names: string[] = [];
    const source = route.pathTemplate.replace(/\{([A-Za-z][A-Za-z0-9]*)\}/gu, (_match: string, name: string) => {
      names.push(name);
      return "([^/]+)";
    });
    return {
      ...route,
      names,
      pattern: new RegExp(`^${escapeStaticPattern(source)}$`, "u"),
    };
  });
}

function matchManifestRoute(
  routes: readonly CompiledManifestRoute[],
  method: string,
  pathname: string,
): { readonly params: Readonly<Record<string, string>>; readonly route: ManifestRoute } | undefined {
  for (const route of routes) {
    if (route.method !== method) {
      continue;
    }
    const match = route.pattern.exec(pathname);
    if (match === null) {
      continue;
    }
    return {
      params: Object.fromEntries(
        route.names.map((name, index) => [name, decodeURIComponent(match[index + 1] ?? "")]),
      ),
      route,
    };
  }
  return undefined;
}

function escapeStaticPattern(value: string): string {
  return value
    .split("([^/]+)")
    .map((part) => part.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&"))
    .join("([^/]+)");
}

async function readBody(request: Request): Promise<unknown> {
  if (request.body === null || request.method === "GET" || request.method === "HEAD") {
    return undefined;
  }
  const text = await request.text();
  if (text.length === 0) {
    return undefined;
  }
  if (new TextEncoder().encode(text).byteLength > 2 * 1024 * 1024) {
    throw new TypeError("The request body exceeds the local emulator's 2 MiB limit.");
  }
  const contentType = request.headers.get("content-type")?.toLowerCase() ?? "";
  if (!contentType.includes("json")) {
    return text;
  }
  try {
    return JSON.parse(text) as unknown;
  } catch {
    throw new TypeError("The request body is not valid JSON.");
  }
}

function isAuthorized(
  request: Request,
  method: string,
  pathname: string,
  bearerToken: string,
): boolean {
  if (isAnonymousRoute(method, pathname)) {
    return true;
  }
  return matchesLocalBearerAuthorization(
    request.headers.get("authorization"),
    bearerToken,
  );
}

function isAnonymousRoute(method: string, pathname: string): boolean {
  return method === "GET"
    && (pathname === "/api/v1/regions" || pathname.startsWith("/api/v1/regions/"));
}

function idempotencyKey(context: LocalRequestContext): string | undefined {
  if (context.pathname.startsWith("/v1/sync/")) {
    return undefined;
  }
  if (!new Set(["DELETE", "PATCH", "POST", "PUT"]).has(context.method)) {
    return undefined;
  }
  const value = context.request.headers.get("idempotency-key")?.trim();
  return value === undefined || value.length === 0 ? undefined : value;
}

function idempotencyScope(context: LocalRequestContext): string {
  const authorization = context.request.headers.get("authorization")?.trim() ?? "";
  const cookie = context.request.headers.get("cookie")?.trim() ?? "";
  const projectId = context.request.headers.get("x-terrascale-project-id")?.trim() ?? "";
  return createHash("sha256")
    .update(authorization)
    .update("\n")
    .update(cookie)
    .update("\n")
    .update(projectId)
    .digest("hex");
}

function requestFingerprint(context: LocalRequestContext): string {
  return createHash("sha256")
    .update(context.method)
    .update("\n")
    .update(context.pathname)
    .update("\n")
    .update(context.url.search)
    .update("\n")
    .update(JSON.stringify(context.body ?? null))
    .digest("hex");
}

function withCors(request: Request, response: Response): Response {
  const origin = request.headers.get("origin");
  if (origin === null || !isLoopbackOrigin(origin)) {
    return response;
  }
  const headers = new Headers(response.headers);
  headers.set("access-control-allow-origin", origin);
  headers.set("access-control-allow-credentials", "true");
  headers.set("access-control-allow-headers", "authorization, content-type, idempotency-key, if-match, x-terrascale-delete-confirmation, x-terrascale-project-id");
  headers.set("access-control-allow-methods", "DELETE, GET, OPTIONS, PATCH, POST, PUT");
  headers.append("vary", "origin");
  return new Response(response.body, { status: response.status, statusText: response.statusText, headers });
}

function isLoopbackOrigin(value: string): boolean {
  try {
    const hostname = new URL(value).hostname;
    return hostname === "127.0.0.1" || hostname === "::1" || hostname === "localhost";
  } catch {
    return false;
  }
}
