import assert from 'node:assert/strict';
import { appendFileSync, writeFileSync } from 'node:fs';
import { execFileSync, spawnSync } from 'node:child_process';
import { github, alerts, alertFor, attempts, closeAlert, reads } from './deploy-alert.mjs';

const [thing, ...args] = process.argv.slice(2);
const { GITHUB_REPOSITORY: repo, CLOUDFLARE_ACCOUNT_ID: account, CLOUDFLARE_API_TOKEN: token } = process.env;
const services = { dashboard: 'https://dash.terrascale.tech/readyz', auth: 'https://auth.terrascale.tech/readyz' };
assert.ok(['all', 'docs', 'landing', 'status', ...Object.keys(services)].includes(thing));
const git = (...argv) => execFileSync('git', argv, { encoding: 'utf8' }).trim();
const sha = (value) => assert.match(value ?? '', /^[0-9a-f]{40}$/, 'Missing or invalid live revision');
async function cloudflare(path) {
  const response = await fetch(`https://api.cloudflare.com/client/v4/accounts/${account}/workers/scripts/${path}`, {
    headers: { authorization: `Bearer ${token}` },
    signal: AbortSignal.timeout(30000),
  });
  const body = await response.json();
  if (response.status === 404 && body.errors?.some((error) => error.code === 10007) && path.endsWith('/deployments')) return null;
  assert.ok(response.ok && body.success, `Cloudflare ${path}: HTTP ${response.status} ${JSON.stringify(body.errors)}`);
  return body.result;
}

async function serviceLive(kind = thing) {
  const response = await fetch(services[kind], { signal: AbortSignal.timeout(30000) });
  assert.ok(response.ok, `${kind}: readiness HTTP ${response.status}`);
  const live = response.headers.get('x-terrascale-revision');
  sha(live);
  const body = await response.json();
  assert.ok(['ok', 'ready'].includes(body.status), `${kind}: not ready`);
  return live;
}

async function workerLive(worker) {
  const result = await cloudflare(`${worker}/deployments`);
  if (result === null) return null;
  const active = result.deployments[0];
  assert.ok(active && active.versions.length === 1 && active.versions[0].percentage === 100, `${worker}: missing or split live deployment`);
  const version = await cloudflare(`${worker}/versions/${active.versions[0].version_id}`);
  const live = version.annotations?.['workers/message'];
  sha(live);
  return live;
}

function ancestor(base, head) {
  const result = spawnSync('git', ['merge-base', '--is-ancestor', base, head]);
  assert.ok(result.status === 0 || result.status === 1, `Cannot compare ${base} to ${head}`);
  return result.status === 0;
}

async function target() {
  for (const revision of git('rev-list', '--first-parent', '-10', 'origin/main').split('\n')) {
    const { workflow_runs: runs } = await github(`actions/workflows/ci.yml/runs?head_sha=${revision}&branch=main&event=push&per_page=1`);
    const run = runs[0];
    if (!run || run.status !== 'completed' || run.conclusion !== 'success') continue;
    assert.ok(run.head_sha === revision && run.head_branch === 'main' && run.event === 'push' && run.head_repository.full_name === repo, 'Unexpected CI identity');
    const { jobs, total_count: count } = await github(`actions/runs/${run.id}/jobs?filter=latest&per_page=100`);
    assert.ok(count <= 100, 'CI jobs exceed the qualification read bound');
    const journey = jobs.filter((job) => job.name.startsWith('journey / journey '));
    if (journey.length >= 2 && journey.every((job) => job.conclusion === 'success') && jobs.some((job) => job.name === 'all-green' && job.conclusion === 'success')) return revision;
  }
  return null;
}

function inputs(thing) {
  const common = ['scripts/ci/site-deploy-gate.mjs', 'scripts/ci/deploy-alert.mjs', ':(exclude,glob)**/AGENTS.md', ':(exclude,glob)**/CLAUDE.md'];
  if (!services[thing]) return [...common, `${thing}-site`, '.github/workflows/sites.yml', 'scripts/ci/prove-worker-deploy.mjs', ...(thing === 'docs' ? ['Makefile', 'landing-site/static/pricing.json', 'landing-site/static/regions.json'] : [])];
  const shared = ['Directory.*', 'global.json', 'NuGet.config', '.editorconfig', 'BannedSymbols.txt', 'Makefile', 'sdk/dotnet', ':(glob)src/TerraScale.Endpoints*/**', 'scripts/ci/reconcile-image.sh', 'scripts/ci/release-app.sh', 'scripts/ci/fetch-ts-orch.sh'];
  return [...common, ...shared, ...(thing === 'auth'
    ? [':(glob)src/TerraScale.Authentication*/**', 'src/TerraScale.Logging', 'auth.slnf', 'Dockerfile', 'scripts/auth', 'scripts/ci/image-payloads.sh', '.github/workflows/ci.yml']
    : [':(glob)src/TerraScale.Dashboard*/**', 'src/TerraScale.TerraBase.Contracts', 'scripts/dashboard', '.github/workflows/images-ghcr.yml', '.github/workflows/dashboard-ci.yml', 'scripts/efcore/check-design-host.py', 'scripts/ci/release-only.sh'])];
}

if (args[0] === '--prove') {
  sha(process.env.REVISION);
  assert.equal(await serviceLive(), process.env.REVISION, `${thing}: deployed revision mismatch`);
  writeFileSync(`${process.env.RUNNER_TEMP}/deploy-revision`, `${process.env.REVISION}\n`);
  console.log(`${thing}: ready at ${process.env.REVISION}`);
} else {
  git('fetch', '--no-tags', 'origin', '+refs/heads/main:refs/remotes/origin/main');
  const groups = thing === 'all'
    ? [['docs', ['ts-docs']], ['landing', ['ts-landing']], ['status', ['ts-status-vantage-sfo', 'ts-status-vantage-gru', 'ts-status']], ['auth', []], ['dashboard', []]]
    : [[thing, args]];
  const live = [];
  for (const [kind, workers] of groups) {
    if (services[kind]) {
      live.push([kind, kind, await serviceLive(kind)]);
    } else {
      for (const worker of workers) live.push([kind, worker, await workerLive(worker)]);
    }
  }
  assert.ok(live.length > 0, 'No deployment selected');
  for (const [, name, revision] of live) {
    assert.ok(revision === null || ancestor(revision, 'origin/main'), `${name}: live ${revision} is not on main`);
  }
  const revision = await target();
  if (revision === null) {
    for (const [, name, deployed] of live) {
      assert.ok(deployed !== null, `${name}: nothing qualified and no live revision`);
      console.log(`${name}: nothing qualified yet, live is ${deployed}`);
    }
    appendFileSync(process.env.GITHUB_OUTPUT, 'deploy=false\ndocs=false\nlanding=false\nstatus=[]\n');
  } else {
    const issues = await alerts();
    const selected = [];
    for (const [kind, name, deployed] of live) {
      const issue = alertFor(issues, name);
      let decision;
      if (deployed === revision) decision = 'already current';
      else if (deployed !== null && ancestor(revision, deployed)) decision = 'live is ahead on main';
      else {
        assert.ok(deployed === null || ancestor(deployed, revision), `${name}: live and target have diverged`);
        decision = deployed === null || git('diff', '--name-only', deployed, revision, '--', ...inputs(kind)) ? 'deploy' : 'inputs unchanged';
      }
      if (decision === 'deploy' && attempts(issue, revision) >= 2) decision = 'waiting for a new target or a closed issue';
      if (decision === 'deploy') selected.push([kind, name]);
      else if (['already current', 'live is ahead on main', 'inputs unchanged'].includes(decision)) await closeAlert(issue, deployed);
      console.log(`${name}: live=${deployed ?? 'absent'} target=${revision} decision=${decision}`);
    }
    if (thing === 'all') {
      for (const service of ['auth', 'dashboard']) {
        if (!selected.some(([kind]) => kind === service)) continue;
        const workflow = service === 'auth' ? 'ci.yml' : 'images-ghcr.yml';
        let active = false;
        for (const status of ['queued', 'in_progress', 'waiting', 'pending', 'requested']) {
          const { workflow_runs: runs } = await github(`actions/workflows/${workflow}/runs?branch=main&event=workflow_dispatch&status=${status}&per_page=1`);
          if (runs.length > 0) {
            active = true;
            break;
          }
        }
        if (active) console.log(`${service}: waiting for the active deploy workflow`);
        else await github(`actions/workflows/${workflow}/dispatches`, 'POST', {
          ref: 'main', inputs: service === 'auth' ? { reconcile_auth: true } : {},
        });
      }
    }
    appendFileSync(process.env.GITHUB_OUTPUT, `deploy=${selected.length > 0}\nworkers=${selected.map(([, name]) => name).join(' ')}\nrevision=${revision}\ndocs=${selected.some(([kind]) => kind === 'docs')}\nlanding=${selected.some(([kind]) => kind === 'landing')}\nstatus=${JSON.stringify(selected.filter(([kind]) => kind === 'status').map(([, name]) => name))}\n`);
    appendFileSync(process.env.GITHUB_ENV, `REVISION=${revision}\n`);
  }
  console.log(`GitHub API reads: ${reads}`);
}
