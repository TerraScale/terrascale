import { errors } from '@playwright/test';

type Operation = 'prepare-auth-management' | 'validate-machine-credentials' | 'validate-tenant-tokens'
  | 'record-user-audit-actions' | 'assert-auth-management-audit' | 'close-owner-context';

export const requiredAuditActions = ['disable_project_user.succeeded', 'enable_project_user.succeeded', 'sign_out_project_user.succeeded'] as const;
type AuditAction = typeof requiredAuditActions[number];
export type AuditEvent = { actor: string; action: string; source: string };
type AuditMismatch = { kind: 'audit-mismatch'; action: AuditAction; rows: number; tsAuthRows: number; actorMatched: boolean };
type Cause = AuditMismatch | { kind: 'assertion' | 'timeout' | 'syntax-error' | 'type-error' | 'error' | 'thrown-value' };
type Failure = { operation: Operation; cause: Cause };

export class AuthJourneyFailure extends Error {
  constructor(readonly failures: readonly Failure[]) {
    super(failures.map(({ operation, cause }) => {
      const step = `${operation} cause=${cause.kind}`;
      return cause.kind === 'audit-mismatch'
        ? `${step} action=${cause.action} rows=${cause.rows} tsAuthRows=${cause.tsAuthRows} actorMatched=${cause.actorMatched}`
        : step;
    }).join('\n'));
    this.name = 'AuthJourneyFailure';
  }
}

export function assertAuthAuditEvent(events: readonly AuditEvent[], action: AuditAction, actor: string): void {
  const matchingAction = events.filter((event) => event.action === action);
  const fromAuth = matchingAction.filter((event) => event.source === 'ts-auth');
  if (fromAuth.some((event) => event.actor === actor)) return;
  throw new AuthJourneyFailure([{
    operation: 'assert-auth-management-audit',
    cause: {
      kind: 'audit-mismatch', action, rows: matchingAction.length,
      tsAuthRows: fromAuth.length,
      actorMatched: matchingAction.some((event) => event.actor === actor),
    },
  }]);
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
