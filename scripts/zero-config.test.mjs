import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import test from 'node:test';

test('application code and CI introduce no switch-style configuration', () => {
  const files = execFileSync('git', ['ls-files', '-z'], { encoding: 'utf8' }).split('\0');
  for (const file of files) {
    if (!/\.(?:[cm]?js|ts|json|ya?ml)$/.test(file) || /(?:\.test\.|\/test\/|package-lock\.json$)/.test(file)) continue;
    const text = readFileSync(file, 'utf8');
    assert.doesNotMatch(text, /\b[A-Z][A-Z0-9_]*(?:ENABLE|DISABLE|SKIP_AUTH|FEATURE_FLAG|DEMO_MODE)[A-Z0-9_]*\b/, file);
    assert.doesNotMatch(text, /\b(?:enabled|disabled|enable[A-Z]\w*|disable[A-Z]\w*|skipAuth|demoMode|featureFlags)\s*:/, file);
  }
});
