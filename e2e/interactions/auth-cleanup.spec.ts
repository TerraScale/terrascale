import { expect, test } from '@playwright/test';
import { assertAuthAuditEvent, AuthJourneyFailure, authStep, withAuthCleanup } from '../support/auth-failures';

test('nested cleanup reports token validation, the exact audit gap and context close separately', async () => {
  const material = 'fixture-actor:fixture-secret';
  const encoded = Buffer.from(material).toString('base64');
  const action = 'disable_project_user.succeeded';
  const actor = 'fixture-actor';
  const rows = [
    { actor, action, source: 'fixture-dashboard' },
    { actor: 'fixture-other-actor', action, source: 'ts-auth' },
    { actor, action: 'enable_project_user.succeeded', source: 'ts-auth' },
  ];
  assertAuthAuditEvent([...rows, { actor, action, source: 'ts-auth' }], action, actor);

  const result = await withAuthCleanup('prepare-auth-management', () => withAuthCleanup('record-user-audit-actions',
    () => authStep('validate-tenant-tokens', async () => { expect(false, `Authorization: Basic ${encoded}`).toBe(true); }),
    'assert-auth-management-audit', async () => { assertAuthAuditEvent(rows, action, actor); }),
  'close-owner-context', async () => { throw new TypeError(`close request failed with raw rows ${JSON.stringify(rows)}`); })
    .catch((error: unknown) => error);

  expect(result).toBeInstanceOf(AuthJourneyFailure);
  if (!(result instanceof AuthJourneyFailure)) throw new Error('expected redacted auth diagnostic');
  expect(result.failures).toEqual([
    { operation: 'validate-tenant-tokens', cause: { kind: 'assertion' } },
    { operation: 'assert-auth-management-audit', cause: { kind: 'audit-mismatch', action, rows: 2, tsAuthRows: 1, actorMatched: true } },
    { operation: 'close-owner-context', cause: { kind: 'type-error' } },
  ]);
  expect(result.message.split('\n')).toEqual([
    'validate-tenant-tokens cause=assertion',
    'assert-auth-management-audit cause=audit-mismatch action=disable_project_user.succeeded rows=2 tsAuthRows=1 actorMatched=true',
    'close-owner-context cause=type-error',
  ]);
  expect(result.cause).toBeUndefined();
  const report = JSON.stringify({ ...result, message: result.message, stack: result.stack });
  for (const raw of [material, encoded, 'Authorization', 'fixture-secret', 'fixture-actor', 'fixture-other-actor', 'fixture-dashboard', 'close request failed']) {
    expect(report.includes(raw)).toBe(false);
  }
});

test('a request exception is redacted even when cleanup succeeds', async () => {
  const result = await withAuthCleanup('validate-tenant-tokens', async () => {
    throw new Error('apiRequestContext.post failed with Authorization: Bearer fixture-secret');
  }, 'close-owner-context', async () => {}).catch((error: unknown) => error);

  expect(result).toBeInstanceOf(AuthJourneyFailure);
  if (!(result instanceof AuthJourneyFailure)) throw new Error('expected redacted auth diagnostic');
  expect(result.failures).toEqual([{ operation: 'validate-tenant-tokens', cause: { kind: 'error' } }]);
  expect(result.message).toBe('validate-tenant-tokens cause=error');
  expect(result.cause).toBeUndefined();
});
