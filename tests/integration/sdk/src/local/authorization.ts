import {
  problemResponse,
  type LocalDatabaseIdentity,
  type LocalPrincipal,
  type LocalRequestContext,
} from "./context.js";
import type { TerraScaleSqliteStore } from "./store.js";

type StoredSession = {
  readonly token?: unknown;
  readonly userId?: unknown;
  readonly expiresAt?: unknown;
};

type StoredProject = {
  readonly projectId?: unknown;
  readonly organizationId?: unknown;
  readonly ownerUserIds?: unknown;
};

type StoredDatabase = {
  readonly databaseId?: unknown;
  readonly id?: unknown;
  readonly name?: unknown;
  readonly projectId?: unknown;
};

const projectNamespace = "projects";
const databaseNamespace = "sdk-local-database";

export type LocalDatabaseResolution = LocalDatabaseIdentity | Response | undefined;

/** Resolve the user identity carried by the Better Auth bearer session. */
export function resolveLocalPrincipal(
  store: Pick<TerraScaleSqliteStore, "list">,
  request: Request,
): LocalPrincipal | undefined {
  const token = bearerToken(request.headers.get("authorization"));
  if (token === undefined) return undefined;

  const sessions = store
    .list<StoredSession>("better-auth", "session")
    .filter((candidate) => sessionMatches(candidate, token) && !sessionExpired(candidate));
  if (sessions.length !== 1) return undefined;
  const session = sessions[0];
  return typeof session?.userId === "string" && session.userId.length > 0
    ? { userId: session.userId }
    : undefined;
}

/**
 * Authorize a direct data route against the local project's existing
 * Better Auth and project ownership records. The permissive loopback runtime
 * deliberately bypasses this check; required authentication always needs a
 * resolved principal and an owned database.
 */
export function authorizeLocalDatabase(
  context: LocalRequestContext,
  database: string,
): Response | undefined {
  const resolved = resolveLocalDatabase(context, database);
  return resolved instanceof Response ? resolved : undefined;
}

/** Resolve and authorize one public database identifier to its stored identity. */
export function resolveLocalDatabase(
  context: LocalRequestContext,
  database: string,
): LocalDatabaseResolution {
  if (context.resolvedDatabase !== undefined) return context.resolvedDatabase;
  if (context.authentication !== "required") return undefined;
  const principal = context.principal;
  if (principal === undefined) {
    return forbidden("A Better Auth session is required for database access.", "principal_required");
  }

  const candidates = context.store
    .listNamespace<StoredDatabase>(databaseNamespace)
    .filter((candidate) => {
      if (!isRecord(candidate.value)) return false;
      const databaseId = text(candidate.value.databaseId) ?? text(candidate.value.id);
      return databaseId === database || text(candidate.value.name) === database;
    });
  const requestedProject = text(context.request.headers.get("x-terrascale-project-id"));
  const identities = new Set(
    candidates
      .map((candidate) => {
        if (!isRecord(candidate.value)) return undefined;
        const projectId = text(candidate.value.projectId);
        const databaseId = text(candidate.value.databaseId) ?? text(candidate.value.id);
        return projectId === undefined || databaseId === undefined
          ? undefined
          : `${projectId}\u0000${databaseId}`;
      })
      .filter((identity): identity is string => identity !== undefined),
  );
  if (identities.size !== 1) {
    return forbidden("The database identity is missing or ambiguous.", "database_forbidden");
  }
  for (const candidate of candidates) {
    const value = candidate.value;
    if (!isRecord(value)) continue;
    const databaseId = text(value.databaseId) ?? text(value.id);
    const projectId = text(value.projectId);
    if (databaseId !== database && text(value.name) !== database) continue;
    if (
      projectId === undefined
      || databaseId === undefined
      || (requestedProject !== undefined && requestedProject !== projectId)
    ) {
      continue;
    }
    if (hasProjectAccess(context.store, projectId, principal.userId)) {
      return {
        databaseId,
        name: text(value.name) ?? database,
        projectId,
      };
    }
  }

  return forbidden("The principal is not authorized for this database.", "database_forbidden");
}

function hasProjectAccess(
  store: Pick<TerraScaleSqliteStore, "get">,
  projectId: string,
  userId: string,
): boolean {
  const project = store.get<StoredProject>(projectNamespace, "local", projectId);
  if (project === undefined || project === null || typeof project !== "object" || Array.isArray(project)) {
    return false;
  }
  if (Array.isArray(project.ownerUserIds) && project.ownerUserIds.includes(userId)) return true;
  if (store.get("team-members", projectId, userId) !== undefined) return true;
  const organizationId = text(project.organizationId);
  return organizationId !== undefined
    && store.get("organization-members", organizationId, userId) !== undefined;
}

function bearerToken(authorization: string | null): string | undefined {
  if (authorization === null) return undefined;
  const match = /^Bearer\s+(\S+)$/iu.exec(authorization.trim());
  return match?.[1];
}

function sessionMatches(session: StoredSession, presented: string): boolean {
  return isRecord(session) && typeof session.token === "string" && (
    presented === session.token || presented.startsWith(`${session.token}.`)
  );
}

function sessionExpired(session: StoredSession): boolean {
  if (!isRecord(session)) return true;
  if (session.expiresAt === undefined || session.expiresAt === null) return false;
  const expiresAt = session.expiresAt instanceof Date
    ? session.expiresAt.getTime()
    : typeof session.expiresAt === "string"
      ? Date.parse(session.expiresAt)
      : Number(session.expiresAt);
  return !Number.isFinite(expiresAt) || expiresAt <= Date.now();
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function text(value: unknown): string | undefined {
  return typeof value === "string" && value.trim().length > 0 ? value.trim() : undefined;
}

function forbidden(message: string, code: string): Response {
  return problemResponse(403, code, message);
}
