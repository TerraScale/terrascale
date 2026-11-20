import { expect, test } from '@playwright/test';
import { AuditGap, HarnessFailure, withCleanup } from '../support/auth-management';
import { saveSecret } from '../support/state';

test('cleanup keeps the primary failure without a derived credential or request text', async () => {
  const material = 'key-id:key-secret';
  const encoded = Buffer.from(material).toString('base64');
  saveSecret('fixture-basic-material', material);
  saveSecret('fixture-basic-credential', encoded);
  const leaked = new Error(`request failed Authorization: Basic ${encoded}`);
  const audit = new AuditGap('disable_project_user.succeeded', 0, 0, false);
  const close = new Error(`account close failed Authorization: Basic ${encoded}`);
  const error = await withCleanup(
    () => withCleanup(async () => { throw leaked; }, async () => { throw audit; }),
    async () => { throw close; },
  ).then(() => { throw new Error('both failures were dropped'); }, (caught: unknown) => caught);
  expect(error).toBeInstanceOf(HarnessFailure);
  const message = error instanceof Error ? error.message : String(error);
  expect(message).toContain('primary category=failure');
  expect(message).toContain('cleanup category=audit action=disable_project_user.succeeded rows=0 sources=0 actorMatched=false');
  expect(message).toContain('category=failure');
  expect(message).not.toContain(encoded);
  expect(message).not.toContain(material);
  expect(message).not.toContain('Authorization');
  expect(message).not.toContain('key-secret');
});
