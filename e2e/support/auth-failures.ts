import { errors, type APIResponse } from '@playwright/test';

type Operation = 'prepare-auth-management' | 'validate-machine-credentials' | 'validate-tenant-tokens'
  | 'remove-tenant-token-database' | 'record-user-audit-actions' | 'assert-auth-management-audit' | 'close-owner-context';

export const requiredAuditActions = ['disable_project_user.succeeded', 'enable_project_user.succeeded', 'sign_out_project_user.succeeded'] as const;
type AuditAction = typeof requiredAuditActions[number];
type AuditEvent = { actor: string; action: string; source: string };
type AuditResponse = Pick<APIResponse, 'status' | 'json'>;
type AuditCause = { kind: 'audit-mismatch'; action: AuditAction; rows: number; tsAuthRows: number; actorMatched: boolean }
  | { kind: 'audit-status'; action: AuditAction; status: number }
  | { kind: 'audit-request' | 'audit-parse'; action: AuditAction };
type Cause = AuditCause | { kind: 'assertion' | 'timeout' | 'syntax-error' | 'type-error' | 'error' | 'thrown-value' };
type Failure = { operation: Operation; cause: Cause };

export class AuthJourneyFailure extends Error {
  constructor(readonly failures: readonly Failure[]) {
    super(failures.map(({ operation, cause }) => {
      const step = `${operation} cause=${cause.kind}`;
      if (!('action' in cause)) return step;
      const audit = `${step} action=${cause.action}`;
      if (cause.kind === 'audit-status') return `${audit} status=${cause.status}`;
      if (cause.kind === 'audit-mismatch') return `${audit} rows=${cause.rows} tsAuthRows=${cause.tsAuthRows} actorMatched=${cause.actorMatched}`;
      return audit;
    }).join('\n'));
    this.name = 'AuthJourneyFailure';
  }
}

function auditFailure(cause: AuditCause): never {
  throw new AuthJourneyFailure([{ operation: 'assert-auth-management-audit', cause }]);
}

function isAuditEvent(value: unknown): value is AuditEvent {
  return typeof value === 'object' && value !== null
    && 'actor' in value && typeof value.actor === 'string'
    && 'action' in value && typeof value.action === 'string'
    && 'source' in value && typeof value.source === 'string';
}

export async function assertAuthAuditEvent(action: AuditAction, actor: string,
  fetch: () => Promise<AuditResponse>): Promise<void> {
  let response: AuditResponse;
  try {
    response = await fetch();
  } catch {
    auditFailure({ kind: 'audit-request', action });
  }
  const status = response.status();
  if (status !== 200) auditFailure({ kind: 'audit-status', action, status });

  let body: unknown;
  try {
    body = await response.json();
  } catch {
    auditFailure({ kind: 'audit-parse', action });
  }
  if (typeof body !== 'object' || body === null || !('events' in body)
    || !Array.isArray(body.events) || !body.events.every(isAuditEvent)) auditFailure({ kind: 'audit-parse', action });
  const events = body.events;
  const matchingAction = events.filter((event) => event.action === action);
  const fromAuth = matchingAction.filter((event) => event.source === 'ts-auth');
  if (fromAuth.some((event) => event.actor === actor)) return;
  auditFailure({
    kind: 'audit-mismatch', action, rows: matchingAction.length,
    tsAuthRows: fromAuth.length, actorMatched: matchingAction.some((event) => event.actor === actor),
  });
}

function safeFailures(operation: Operation, error: unknown): readonly Failure[] {
  if (error instanceof AuthJourneyFailure) return error.failures;
  if (error instanceof Error && 'matcherResult' in error) return [{ operation, cause: { kind: 'assertion' } }];
  if (error instanceof errors.TimeoutError) return [{ operation, cause: { kind: 'timeout' } }];
  if (error instanceof SyntaxError) return [{ operation, cause: { kind: 'syntax-error' } }];
  if (error instanceof TypeError) return [{ operation, cause: { kind: 'type-error' } }];
  return [{ operation, cause: { kind: error instanceof Error ? 'error' : 'thrown-value' } }];
}

export async function authStep<T>(operation: Operation, run: () => Promise<T>): Promise<T> {
  try {
    return await run();
  } catch (error) {
    throw new AuthJourneyFailure(safeFailures(operation, error));
  }
}

export async function withAuthCleanup(bodyOperation: Operation, body: () => Promise<void>,
  cleanupOperation: Operation, cleanup: () => Promise<void>): Promise<void> {
  const failures: Failure[] = [];
  try {
    await body();
  } catch (error) {
    failures.push(...safeFailures(bodyOperation, error));
  }
  try {
    await cleanup();
  } catch (error) {
    failures.push(...safeFailures(cleanupOperation, error));
  }
  if (failures.length > 0) throw new AuthJourneyFailure(failures);
}
