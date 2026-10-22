import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import test from 'node:test';
import { secureHtml } from './security-headers.mjs';

test('CSP allows only exact built inline scripts and precedes every script', () => {
  const script = '\nwindow.ready = true;\n';
  const html = `<html><head><script>${script}</script><script src="/app.js"></script></head><body></body></html>`;
  const secured = secureHtml(html);
  const hash = createHash('sha256').update(script).digest('base64');
  assert.ok(secured.includes(`'sha256-${hash}'`));
  assert.ok(secured.indexOf('Content-Security-Policy') < secured.indexOf('<script'));
  assert.ok(secured.includes(`script-src 'self'`));
  assert.ok(!secured.match(/script-src[^;]*'unsafe-inline'/));
  assert.ok(secured.includes("frame-src 'none'"));
  assert.ok(secured.includes("form-action 'none'"));
  assert.throws(() => secureHtml('<body></body>'), /Missing HTML head/);
});

test('script hashes respect HTML end-tag delimiters and trailing attributes', () => {
  const source = 'const marker = "</script-example>"; window.ready = true;';
  const next = 'window.next = true;';
  for (const closing of ['</script\t\n bar>', '</SCRIPT data-end="yes">', '</script/>']) {
    const secured = secureHtml(`<html><head><script>${source}${closing}<script>${next}</script></head></html>`);
    for (const script of [source, next]) {
      const hash = createHash('sha256').update(script).digest('base64');
      assert.ok(secured.includes(`'sha256-${hash}'`), closing);
    }
  }
});
