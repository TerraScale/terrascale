import assert from 'node:assert/strict';
import { appendFileSync, writeFileSync } from 'node:fs';
import { execFileSync, spawnSync } from 'node:child_process';

const [thing, ...args] = process.argv.slice(2);
const { GITHUB_REPOSITORY: repo, CLOUDFLARE_ACCOUNT_ID: account, CLOUDFLARE_API_TOKEN: token } = process.env;
const services = { dashboard: 'https://dash.terrascale.tech/readyz', auth: 'https://auth.terrascale.tech/readyz' };
const label = 'deployment-alert';
const bot = 'github-actions[bot]';
const runLink = `${process.env.GITHUB_SERVER_URL}/${repo}/actions/runs/${process.env.GITHUB_RUN_ID}`;
const activeLimit = 2 * 60 * 60 * 1000;
assert.ok(['all', 'docs', 'landing', 'status', ...Object.keys(services)].includes(thing));
let reads = 0;
const git = (...argv) => execFileSync('git', argv, { encoding: 'utf8' }).trim();
const sha = (value) => assert.match(value ?? '', /^[0-9a-f]{40}$/, 'Missing or invalid live revision');
const report = (line) => {
  console.log(line);
  appendFileSync(process.env.GITHUB_STEP_SUMMARY, `- ${line}\n`);
};

async function github(path, method = 'GET', body) {
  if (method === 'GET') reads++;
  const response = await fetch(`https://api.github.com/repos/${repo}/${path}`, {
    method,
    headers: { authorization: `Bearer ${process.env.GH_TOKEN}`, accept: 'application/vnd.github+json' },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(30000),
  });
  const existingLabel = path === 'labels' && response.status === 422;
  assert.ok(response.ok || existingLabel, `GitHub ${method} ${path}: HTTP ${response.status}`);
  return response.status === 204 || existingLabel ? null : response.json();
}

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
  const common = ['scripts/ci/site-deploy-gate.mjs', ':(exclude,glob)**/AGENTS.md', ':(exclude,glob)**/CLAUDE.md'];
  if (!services[thing]) return [...common, `${thing}-site`, '.github/workflows/sites.yml', 'scripts/ci/prove-worker-deploy.mjs', ...(thing === 'docs' ? ['Makefile', 'landing-site/static/pricing.json', 'landing-site/static/regions.json'] : [])];
  const shared = ['Directory.*', 'global.json', 'NuGet.config', '.editorconfig', 'BannedSymbols.txt', 'Makefile', 'sdk/dotnet', ':(glob)src/TerraScale.Endpoints*/**', 'scripts/ci/reconcile-image.sh', 'scripts/ci/release-app.sh', 'scripts/ci/fetch-ts-orch.sh'];
  return [...common, ...shared, ...(thing === 'auth'
    ? [':(glob)src/TerraScale.Authentication*/**', 'src/TerraScale.Logging', 'auth.slnf', 'Dockerfile', 'scripts/auth', 'scripts/ci/image-payloads.sh', '.github/workflows/ci.yml']
    : [':(glob)src/TerraScale.Dashboard*/**', 'src/TerraScale.TerraBase.Contracts', 'scripts/dashboard', '.github/workflows/images-ghcr.yml', '.github/workflows/dashboard-ci.yml', 'scripts/efcore/check-design-host.py', 'scripts/ci/release-only.sh'])];
}

function work(kind, name, deployed, revision) {
  if (deployed === revision) return 'already live';
  if (deployed !== null && ancestor(revision, deployed)) return 'live is ahead on main';
  assert.ok(deployed === null || ancestor(deployed, revision), `${name}: live and target have diverged`);
  return deployed === null || git('diff', '--name-only', deployed, revision, '--', ...inputs(kind)) ? 'deploy' : 'inputs unchanged';
}

async function alerts() {
  const issues = [];
  for (let page = 1; ; page++) {
    const batch = await github(`issues?state=open&labels=${label}&per_page=100&page=${page}`);
    issues.push(...batch);
    if (batch.length < 100) return issues;
  }
}

function alertFor(issues, name) {
  const title = `Deployment attempts: ${name}`;
  const matches = issues.filter((issue) => issue.title === title);
  assert.ok(matches.length <= 1, `${name}: ${matches.length} open deployment alerts (${matches.map((issue) => issue.html_url).join(', ')}); close all but one`);
  if (matches.length === 0) return null;
  const [issue] = matches;
  const where = `${name}: deployment alert ${issue.html_url}`;
  assert.ok(!issue.pull_request, `${where} is a pull request`);
  assert.ok(issue.user?.login === bot && issue.user?.type === 'Bot', `${where} was not created by ${bot}; close it`);
  assert.ok(issue.labels.some((entry) => entry.name === label), `${where} lacks the ${label} label`);
  const body = (issue.body ?? '').replaceAll('\r\n', '\n');
  const blocks = [...body.matchAll(/^```json\n(.*)\n```$/gm)];
  assert.ok(blocks.length === 1 && body.match(/^```/gm).length === 2, `${where} must hold exactly one fenced state block; close it`);
  let state = null;
  try {
    state = JSON.parse(blocks[0][1]);
  } catch {
    assert.fail(`${where} holds a state block that is not JSON; close it`);
  }
  assert.ok(state !== null && typeof state === 'object' && !Array.isArray(state)
    && Object.keys(state).toSorted().join() === 'attempts,target,thing', `${where} state must have exactly thing, target and attempts; close it`);
  assert.ok(state.thing === name, `${where} records thing ${JSON.stringify(state.thing)}; close it`);
  assert.ok(typeof state.target === 'string' && /^[0-9a-f]{40}$/.test(state.target), `${where} records an invalid target; close it`);
  assert.ok(Number.isInteger(state.attempts) && state.attempts >= 1 && state.attempts <= 2, `${where} records ${JSON.stringify(state.attempts)} attempts; close it`);
  return { issue, body, target: state.target, attempts: state.attempts };
}

function next(alert, revision) {
  if (alert === null) return 1;
  if (revision === alert.target) return alert.attempts < 2 ? alert.attempts + 1 : 'suppressed after two attempts';
  if (ancestor(alert.target, revision)) return 1;
  return `waiting: the selected target is not newer than the recorded failed one (${alert.target})`;
}

const covers = (alert, revision) => alert !== null && (revision === alert.target || ancestor(alert.target, revision));

async function count(alert, name, revision, attempts) {
  const kept = alert !== null && alert.target === revision ? alert.body.split('\n').filter((line) => line.startsWith('Attempt ')) : [];
  const body = [
    `Deployment attempts of \`${name}\`, each counted before it starts. The next reconcile closes this issue once live serves this target or a newer one. After two attempts at one commit, deployments of \`${name}\` stop until a newer commit qualifies or this issue is closed.`,
    '',
    '```json',
    JSON.stringify({ thing: name, target: revision, attempts }),
    '```',
    '',
    ...kept,
    `Attempt ${attempts} at ${revision}: ${runLink}`,
    '',
  ].join('\n');
  if (alert !== null) {
    await github(`issues/${alert.issue.number}`, 'PATCH', { body });
  } else {
    await github('labels', 'POST', { name: label, color: 'b60205' });
    await github('issues', 'POST', { title: `Deployment attempts: ${name}`, labels: [label], body });
  }
}

async function close(alert, deployed) {
  await github(`issues/${alert.issue.number}`, 'PATCH', {
    state: 'closed',
    body: `${alert.body.trimEnd()}\n\nClosed: live is ${deployed}. ${runLink}\n`,
  });
}

if (args[0] === '--prove') {
  sha(process.env.REVISION);
  assert.equal(await serviceLive(), process.env.REVISION, `${thing}: deployed revision mismatch`);
  writeFileSync(`${process.env.RUNNER_TEMP}/deploy-revision`, `${process.env.REVISION}\n`);
  report(`${thing}: deployed ${process.env.REVISION}`);
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
  if (thing === 'all') {
    const revision = await target();
    const selected = [];
    const failures = [];
    if (revision === null) {
      for (const [, name, deployed] of live) {
        assert.ok(deployed !== null, `${name}: nothing qualified and no live revision`);
        report(`${name}: live ${deployed}, no qualified target`);
      }
    } else {
      const issues = await alerts();
      for (const [kind, name, deployed] of live) {
        const decision = work(kind, name, deployed, revision);
        let note;
        let due;
        try {
          const alert = alertFor(issues, name);
          if (decision === 'deploy') {
            const attempt = next(alert, revision);
            due = typeof attempt === 'number';
            note = due ? `deploy job starts for attempt ${attempt}` : attempt;
          } else {
            due = covers(alert, revision);
            note = due ? `${decision}; deploy job starts to close the alert` : decision;
          }
        } catch (error) {
          due = false;
          note = `not started: ${error.message}`;
          failures.push(error.message);
        }
        if (due) selected.push([kind, name]);
        report(`${name}: live ${deployed ?? 'absent'}, target ${revision}: ${note}`);
      }
      for (const service of ['auth', 'dashboard']) {
        if (!selected.some(([kind]) => kind === service)) continue;
        const workflow = service === 'auth' ? 'ci.yml' : 'images-ghcr.yml';
        const active = [];
        for (const status of ['queued', 'in_progress', 'waiting', 'pending', 'requested']) {
          const { workflow_runs: runs } = await github(`actions/workflows/${workflow}/runs?branch=main&event=workflow_dispatch&status=${status}&per_page=100`);
          active.push(...runs);
        }
        if (active.length === 0) {
          await github(`actions/workflows/${workflow}/dispatches`, 'POST', { ref: 'main', inputs: service === 'auth' ? { reconcile_auth: true } : {} });
          report(`${service}: requested its locked deploy job in ${workflow}`);
          continue;
        }
        const oldest = active.toSorted((left, right) => Date.parse(left.created_at) - Date.parse(right.created_at))[0];
        report(`${service}: waiting for the manual ${workflow} run ${oldest.html_url}, active since ${oldest.created_at}`);
        if (Date.now() - Date.parse(oldest.created_at) > activeLimit) failures.push(`${service}: ${oldest.html_url} has been active for more than two hours`);
      }
    }
    appendFileSync(process.env.GITHUB_OUTPUT, `docs=${selected.some(([kind]) => kind === 'docs')}\nlanding=${selected.some(([kind]) => kind === 'landing')}\nstatus=${JSON.stringify(selected.filter(([kind]) => kind === 'status').map(([, name]) => name))}\n`);
    console.log(`GitHub API reads: ${reads}`);
    assert.ok(failures.length === 0, failures.join('; '));
  } else {
    assert.ok(live.length === 1, 'A locked deploy job reconciles exactly one thing');
    const [[kind, name, deployed]] = live;
    const alert = alertFor(await alerts(), name);
    const revision = await target();
    let deploy = false;
    if (revision === null) {
      assert.ok(deployed !== null, `${name}: nothing qualified and no live revision`);
      report(`${name}: live ${deployed}, no qualified target`);
    } else {
      const decision = work(kind, name, deployed, revision);
      if (decision !== 'deploy') {
        if (covers(alert, revision)) {
          await close(alert, deployed);
          report(`${name}: live ${deployed}, target ${revision}: ${decision}; closed ${alert.issue.html_url}`);
        } else {
          report(`${name}: live ${deployed}, target ${revision}: ${alert === null ? decision : next(alert, revision)}`);
        }
      } else {
        const attempt = next(alert, revision);
        if (typeof attempt === 'number') {
          await count(alert, name, revision, attempt);
          deploy = true;
          report(`${name}: live ${deployed ?? 'absent'}, target ${revision}: deploying, attempt ${attempt} of 2 recorded`);
        } else {
          report(`${name}: live ${deployed ?? 'absent'}, target ${revision}: ${attempt}`);
        }
      }
    }
    appendFileSync(process.env.GITHUB_OUTPUT, `deploy=${deploy}\n`);
    if (deploy) appendFileSync(process.env.GITHUB_ENV, `REVISION=${revision}\n`);
    console.log(`GitHub API reads: ${reads}`);
  }
}
