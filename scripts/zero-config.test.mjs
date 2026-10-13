import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const environmentSwitch = /\b(?:[A-Z0-9_]*_)?(?:ENABLE|DISABLE|SKIP_AUTH|FEATURE_FLAG|DEMO_MODE)(?:_[A-Z0-9]+)*\b/;
const configurationSwitch = /\b(?:enabled|disabled|enable[A-Z]\w*|disable[A-Z]\w*|skipAuth|demoMode|featureFlags)["']?\s*:/;

test('switch gate catches environment names and quoted configuration keys', () => {
  for (const value of ['ENABLE_IMPORT', 'APP_DISABLE_AUTH', 'SKIP_AUTH']) assert.match(value, environmentSwitch);
  for (const value of ['enabled: true', '"enableImport": false', "'skipAuth': true"]) assert.match(value, configurationSwitch);
  // cf's required preview context describes the deployment target, not a feature switch.
  assert.doesNotMatch('CLOUDFLARE_PREVIEW_BUILD', environmentSwitch);
});

test('application code and CI introduce no switch-style configuration', () => {
  const files = execFileSync('git', ['ls-files', '-z'], { encoding: 'utf8' }).split('\0');
  for (const file of files) {
    if (!/\.(?:[cm]?js|ts|json|ya?ml)$/.test(file) || /(?:\.test\.|\/test\/|package-lock\.json$)/.test(file)) continue;
    const text = readFileSync(file, 'utf8');
    assert.doesNotMatch(text, environmentSwitch, file);
    assert.doesNotMatch(text, configurationSwitch, file);
  }
});
