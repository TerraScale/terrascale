import { expect, test } from '@playwright/test';
import { assertAuthAuditEvent, AuthJourneyFailure, authStep, withAuthCleanup } from '../support/auth-failures';
import { checkTenantTokens } from '../support/tenant-tokens';

const reply = (status: number, body: unknown = null) => ({ status: () => status, json: async () => body });

test('nested cleanup reports token validation, database removal, the audit gap and context close separately', async () => {
  const material = 'fixture-actor:fixture-secret';
  const encoded = Buffer.from(material).toString('base64');
  const action = 'disable_project_user.succeeded';
  const actor = 'fixture-actor';
  const rows = [
    { actor, action, source: 'fixture-dashboard' },
    { actor: 'fixture-other-actor', action, source: 'ts-auth' },
    { actor, action: 'enable_project_user.succeeded', source: 'ts-auth' },
  ];
  await assertAuthAuditEvent(action, actor, async () => reply(200, { events: [...rows, { actor, action, source: 'ts-auth' }] }));

  const project = 'fixture-project';
  const machine = 'fixture-machine-token';
  const projectToken = 'fixture-project-token';
  const claims = { sub: actor, aud: `https://api.terrascale.tech/v1/projects/${project}` };
  const user = `header.${Buffer.from(JSON.stringify(claims)).toString('base64url')}.signature`;
  const requests: string[] = [];
  const context: Parameters<typeof checkTenantTokens>[0] = {
    post: async (url) => {
      if (url.endsWith('/oauth/token')) return reply(200, { access_token: projectToken });
      if (url.endsWith('/databases')) return reply(201, { id: 'tenant-fixture' });
      if (url.endsWith('/sql/execute')) return reply(200);
      throw new Error('unexpected fixture request');
    },
    put: async () => reply(200),
    get: async (url, options) => {
      if (options?.headers?.Authorization === `Bearer ${machine}`) requests.push('machine-read');
      return reply(url.includes('/other-project/') ? 401 : 307);
    },
    delete: async () => {
      requests.push('database-delete');
      return reply(503);
    },
  };

  const result = await withAuthCleanup('prepare-auth-management', () => withAuthCleanup('record-user-audit-actions',
    () => authStep('validate-tenant-tokens', () => checkTenantTokens(context, project, 'other-project', material, { user, machine })),
    'assert-auth-management-audit', () => assertAuthAuditEvent(action, actor, async () => reply(200, { events: rows }))),
  'close-owner-context', async () => { throw new TypeError(`close request failed with raw rows ${JSON.stringify(rows)}`); })
    .catch((error: unknown) => error);

  expect(requests).toEqual(['machine-read', 'database-delete']);
  expect(result).toBeInstanceOf(AuthJourneyFailure);
  if (!(result instanceof AuthJourneyFailure)) throw new Error('expected redacted auth diagnostic');
  expect(result.failures).toEqual([
    { operation: 'validate-tenant-tokens', cause: { kind: 'assertion' } },
    { operation: 'remove-tenant-token-database', cause: { kind: 'assertion' } },
    { operation: 'assert-auth-management-audit', cause: { kind: 'audit-mismatch', action, rows: 2, tsAuthRows: 1, actorMatched: true } },
    { operation: 'close-owner-context', cause: { kind: 'type-error' } },
  ]);
  expect(result.message.split('\n')).toEqual([
    'validate-tenant-tokens cause=assertion',
    'remove-tenant-token-database cause=assertion',
    'assert-auth-management-audit cause=audit-mismatch action=disable_project_user.succeeded rows=2 tsAuthRows=1 actorMatched=true',
    'close-owner-context cause=type-error',
  ]);
  expect(result.cause).toBeUndefined();
  const report = JSON.stringify({ ...result, message: result.message, stack: result.stack });
  for (const raw of [material, encoded, user, machine, projectToken, 'Authorization', 'fixture-secret', actor,
    'fixture-other-actor', 'fixture-dashboard', 'close request failed']) {
    expect(report.includes(raw)).toBe(false);
  }
});

test('audit request and parse failures retain the expected action and safe category', async () => {
  const action = 'enable_project_user.succeeded';
  const raw = 'Authorization: Basic fixture-key-material';
  const checks = [
    { fetch: async () => { throw new Error(raw); }, cause: { kind: 'audit-request', action } },
    { fetch: async () => reply(503, raw), cause: { kind: 'audit-status', action, status: 503 } },
    { fetch: async () => ({ status: () => 200, json: async () => { throw new SyntaxError(raw); } }), cause: { kind: 'audit-parse', action } },
    { fetch: async () => reply(200, { events: [raw] }), cause: { kind: 'audit-parse', action } },
  ];
  for (const { fetch, cause } of checks) {
    const result = await assertAuthAuditEvent(action, 'fixture-actor', fetch).catch((error: unknown) => error);
    expect(result).toBeInstanceOf(AuthJourneyFailure);
    if (!(result instanceof AuthJourneyFailure)) throw new Error('expected redacted auth diagnostic');
    expect(result.failures).toEqual([{ operation: 'assert-auth-management-audit', cause }]);
    expect(result.message).toBe(`assert-auth-management-audit cause=${cause.kind} action=${action}${'status' in cause ? ' status=503' : ''}`);
    expect(result.stack?.includes(raw)).toBe(false);
    expect(result.cause).toBeUndefined();
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
