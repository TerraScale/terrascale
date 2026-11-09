import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const terrabase = join(root, 'src', 'TerraBase');

const failures = [
  'NotFound', 'Conflict', 'Validation', 'PreconditionFailed', 'Unauthorized', 'Forbidden', 'QuotaExceeded',
  'Unavailable', 'PayloadTooLarge', 'UnsupportedMediaType', 'RequestTimeout', 'Gone', 'OutcomeUnknown',
];
const defaultCode = {
  NotFound: 'not_found', Conflict: 'conflict', Validation: 'invalid_request', PreconditionFailed: 'precondition_failed',
  Unauthorized: 'unauthorized', Forbidden: 'forbidden', QuotaExceeded: 'quota_exceeded', Unavailable: 'unavailable',
  PayloadTooLarge: 'payload_too_large', UnsupportedMediaType: 'unsupported_media_type', RequestTimeout: 'request_timeout',
  Gone: 'gone', OutcomeUnknown: 'write_outcome_unknown',
};

const csFiles = [];
const walk = (dir) => {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) walk(path);
    else if (entry.name.endsWith('.cs')) csFiles.push(path);
  }
};
walk(terrabase);

const callText = (text, open) => {
  let depth = 0;
  for (let i = open; i < text.length; i++) {
    const c = text[i];
    if (c === '"') {
      i++;
      while (i < text.length && text[i] !== '"') i += text[i] === '\\' ? 2 : 1;
      continue;
    }
    if (c === '(') depth++;
    else if (c === ')') {
      depth--;
      if (depth === 0) return text.slice(open, i + 1);
    }
  }
  return null;
};

const isCode = (value) => /^[a-z_]+$/.test(value);
const lastCode = (call) => {
  const literals = [...call.matchAll(/"([^"]*)"/g)].map((m) => m[1]);
  for (let i = literals.length - 1; i >= 0; i--) if (isCode(literals[i])) return literals[i];
  return null;
};

export const errorCodes = () => {
  const codes = new Set();
  const ctorRe = new RegExp(`new (?:${failures.join('|')})\\(`, 'g');
  const bareRe = /\bnew\(/g;
  const writeRe = /WriteAsync\([^;]*?"([a-z_]+)"/g;
  for (const path of csFiles) {
    const text = readFileSync(path, 'utf8');
    for (const m of text.matchAll(ctorRe)) {
      const call = callText(text, m.index + m[0].length - 1);
      if (!call) continue;
      codes.add(lastCode(call) ?? defaultCode[m[0].slice(4, -1)]);
    }
    for (const m of text.matchAll(bareRe)) {
      const before = text.slice(Math.max(0, m.index - 120), m.index);
      if (!failures.some((f) => new RegExp(`\\b${f}\\b`).test(before))) continue;
      const call = callText(text, m.index + m[0].length - 1);
      if (call) {
        const code = lastCode(call);
        if (code) codes.add(code);
      }
    }
    for (const m of text.matchAll(writeRe)) codes.add(m[1]);
  }
  for (const path of [
    'Errors/Errors.cs', 'Errors/ErrorResponse.cs', 'Features/Sql/SqlErrors.cs', 'Hosting/BadRequests.cs',
  ]) {
    const text = readFileSync(join(terrabase, path), 'utf8');
    for (const m of text.matchAll(/"([a-z_]+)"/g)) codes.add(m[1]);
  }
  return codes;
};

test('every TerraBase error code has an errors.md entry', () => {
  const guide = readFileSync(new URL('../docs/guides/errors.md', import.meta.url), 'utf8');
  const headings = new Set([...guide.matchAll(/^### `([a-z_]+)`$/gm)].map((m) => m[1]));
  const codes = errorCodes();
  const missing = [...codes].filter((code) => !headings.has(code)).sort();
  assert.deepEqual(missing, [], `codes without an errors.md entry: ${missing.join(', ')}`);
  console.log(`error codes documented: ${headings.size}, extracted from TerraBase: ${codes.size}`);
});
