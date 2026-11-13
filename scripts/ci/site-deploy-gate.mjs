import assert from 'node:assert/strict';
import { appendFileSync } from 'node:fs';
import { execFileSync, spawnSync } from 'node:child_process';

const [site, ...workers] = process.argv.slice(2);
const { CLOUDFLARE_ACCOUNT_ID: account, CLOUDFLARE_API_TOKEN: token, REVISION: revision } = process.env;
assert.ok(['docs', 'landing', 'status'].includes(site));
assert.ok(workers.length > 0);
assert.match(revision ?? '', /^[0-9a-f]{40}$/);

async function cloudflare(path) {
  const response = await fetch(`https://api.cloudflare.com/client/v4/accounts/${account}/workers/scripts/${path}`, {
    headers: { authorization: `Bearer ${token}` },
  });
  const body = await response.json();
  if (response.status === 404 && path.endsWith('/deployments')) return null;
  assert.ok(body.success, `GET ${path}: HTTP ${response.status} ${JSON.stringify(body.errors)}`);
  return body.result;
}

const selected = [];
for (const worker of workers) {
  const deployments = await cloudflare(`${worker}/deployments`);
  const active = deployments?.deployments[0];
  let changed = !active || active.versions.length === 0;
  for (const version of active?.versions ?? []) {
    if (version.percentage === 0) continue;
    const { annotations } = await cloudflare(`${worker}/versions/${version.version_id}`);
    const live = annotations?.['workers/message'];
    if (!live) {
      changed = true;
      continue;
    }
    assert.match(live, /^[0-9a-f]{40}$/, `${worker}: invalid live revision`);
    if (live === revision) {
      changed = false;
      break;
    }
    const ancestry = spawnSync('git', ['merge-base', '--is-ancestor', live, revision]);
    assert.ok(ancestry.status === 0 || ancestry.status === 1, `${worker}: cannot compare ${live} to ${revision}`);
    if (ancestry.status === 1) {
      console.log(`${worker}: ${revision} does not advance live ${live}`);
      changed = false;
      break;
    }
    const paths = [`${site}-site`, 'scripts/ci/site-deploy-gate.mjs', 'scripts/ci/prove-worker-deploy.mjs', '.github/workflows/sites.yml',
      ':(exclude,glob)**/AGENTS.md', ':(exclude,glob)**/CLAUDE.md'];
    if (site === 'docs') paths.push('Makefile');
    changed ||= execFileSync('git', ['diff', '--name-only', live, revision, '--', ...paths], { encoding: 'utf8' }).trim().length > 0;
  }
  if (changed) selected.push(worker);
  console.log(`${worker}: ${changed ? 'deploy' : 'already current, superseded, or no site changes'} ${revision}`);
}
appendFileSync(process.env.GITHUB_OUTPUT, `deploy=${selected.length > 0}
workers=${selected.join(' ')}
`);
