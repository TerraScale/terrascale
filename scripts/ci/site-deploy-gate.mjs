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
  if (response.status === 204 || existingLabel) return null;
  const text = await response.text();
  return text === '' ? null : JSON.parse(text);
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
    const current = revision === process.env.GITHUB_SHA && run && String(run.id) === process.env.GITHUB_RUN_ID && run.status === 'in_progress';
    if (!run || (!current && (run.status !== 'completed' || run.conclusion !== 'success'))) continue;
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
  const issues = new Map();
  for (const query of [`creator=${encodeURIComponent(bot)}`, `labels=${label}`]) {
    for (let page = 1; ; page++) {
      const batch = await github(`issues?state=all&${query}&per_page=100&page=${page}`);
      for (const issue of batch) issues.set(issue.number, issue);
      if (batch.length < 100) break;
    }
  }
  return [...issues.values()];
}

function alertFor(issues, name) {
  const names = new RegExp(`(?<![a-zA-Z0-9_-])${name}(?![a-zA-Z0-9_-])`);
  const matches = issues.filter((issue) => {
    const body = (issue.body ?? '').replaceAll('\r\n', '\n');
    const blocks = [...body.matchAll(/^```[^\n]*\n([\s\S]*?)^```/gm)];
    return names.test(issue.title) || blocks.some((block) => new RegExp(`"thing"\\s*:\\s*"${name}"`).test(block[1]))
      || (issue.labels.some((entry) => entry.name === label) && names.test(`${issue.title}\n${body}`));
  });
  assert.ok(matches.length <= 1, `${name}: multiple deployment state candidates (${matches.map((issue) => issue.html_url).join(', ')}); restore exactly one state issue`);
  if (matches.length === 0) return null;
  const [issue] = matches;
  const where = `${name}: deployment state ${issue.html_url}`;
  assert.equal(issue.title, `Deployment state: ${name}`, `${where} has an invalid title; restore it`);
  assert.ok(!issue.pull_request, `${where} is a pull request`);
  assert.ok(issue.user?.login === bot && issue.user?.type === 'Bot', `${where} was not created by ${bot}`);
  assert.ok(issue.labels.some((entry) => entry.name === label), `${where} lacks the ${label} label; restore it`);
  assert.equal(issue.state, 'open', `${where} is closed; reopen it`);
  const body = (issue.body ?? '').replaceAll('\r\n', '\n');
  const blocks = [...body.matchAll(/^```json\n([\s\S]*?)\n```$/gm)];
  assert.ok(blocks.length === 1 && (body.match(/^```/gm) ?? []).length === 2, `${where} must hold exactly one fenced state block`);
  let state = null;
  try {
    state = JSON.parse(blocks[0][1]);
  } catch {
    assert.fail(`${where} holds a state block that is not JSON`);
  }
  assert.ok(state !== null && typeof state === 'object' && !Array.isArray(state)
    && Object.keys(state).toSorted().join() === 'attempts,notified,target,thing', `${where} state must have exactly thing, target, attempts and notified`);
  assert.ok(state.thing === name, `${where} records thing ${JSON.stringify(state.thing)}`);
  assert.ok(typeof state.target === 'string' && /^[0-9a-f]{40}$/.test(state.target), `${where} records an invalid target`);
  assert.ok(Number.isInteger(state.attempts) && state.attempts >= 0 && state.attempts <= 2, `${where} records ${JSON.stringify(state.attempts)} attempts`);
  assert.equal(typeof state.notified, 'boolean', `${where} records an invalid notified value`);
  return { issue, body, ...state };
}

function next(alert, revision) {
  if (alert === null) return 1;
  if (revision === alert.target) return alert.attempts < 2 ? alert.attempts + 1 : 'suppressed after two attempts';
  if (ancestor(alert.target, revision)) return 1;
  return `waiting: the selected target is not newer than the recorded one (${alert.target})`;
}

const healthy = (alert, revision) => alert !== null && alert.target === revision && alert.attempts === 0 && !alert.notified;

function due(alert, deployed, revision, decision) {
  if (deployed === revision) return !healthy(alert, revision);
  const attempt = next(alert, revision);
  if (typeof attempt !== 'number') return attempt === 'suppressed after two attempts' && !alert.notified;
  return decision === 'deploy' || alert === null;
}

async function writeState(alert, name, revision, attempts, notified, started = false) {
  const kept = alert !== null && alert.target === revision && attempts > 0
    ? alert.body.split('\n').filter((line) => line.startsWith('Attempt ')) : [];
  const body = [
    `Persistent deployment state for \`${name}\`. Attempts are counted before deployment starts. After two attempts, land a newer qualified commit or run Sites manually with reset_thing=${name}. Keep this issue open; closing or editing it is validated and does not release suppression.`,
    '',
    '```json',
    JSON.stringify({ thing: name, target: revision, attempts, notified }),
    '```',
    '',
    ...kept,
    ...(started ? [`Attempt ${attempts} at ${revision}: ${runLink}`] : []),
    '',
  ].join('\n');
  if (alert !== null) {
    await github(`issues/${alert.issue.number}`, 'PATCH', { body });
  } else {
    await github('labels', 'POST', { name: label, color: 'b60205' });
    await github('issues', 'POST', { title: `Deployment state: ${name}`, labels: [label], body });
  }
}

async function notify(alert, name, revision) {
  const runs = alert.body.split('\n').filter((line) => line.startsWith('Attempt '));
  await writeState(alert, name, revision, 2, true);
  await github(`issues/${alert.issue.number}/comments`, 'POST', {
    body: `${name}: deployment of ${revision} is suppressed after two attempts.\n\n${runs.join('\n') || 'Attempt run links are unavailable.'}\n\nLand a newer commit that passes main CI, or run Sites manually on main with reset_thing=${name} after fixing the failure. Keep this issue open.`,
  });
}

if (args[0] === '--prove') {
  sha(process.env.REVISION);
  assert.equal(await serviceLive(), process.env.REVISION, `${thing}: deployed revision mismatch`);
  writeFileSync(`${process.env.RUNNER_TEMP}/deploy-revision`, `${process.env.REVISION}\n`);
  report(`${thing}: deployed ${process.env.REVISION}`);
} else if (args[0] === '--reset') {
  const name = process.env.RESET_THING;
  assert.ok(['ts-docs', 'ts-landing', 'ts-status-vantage-sfo', 'ts-status-vantage-gru', 'ts-status', ...Object.keys(services)].includes(name), 'Unknown reset thing');
  const alert = alertFor(await alerts(), name);
  assert.ok(alert !== null, `${name}: no deployment state to reset`);
  await writeState(alert, name, alert.target, 0, false);
  report(`${name}: reset ${alert.target} to zero attempts; next reconcile may deploy`);
  console.log(`GitHub API reads: ${reads}`);
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
  const issues = await alerts();
  const states = new Map();
  const failures = [];
  for (const [, name] of live) {
    try {
      states.set(name, alertFor(issues, name));
    } catch (error) {
      failures.push(error.message);
      report(`${name}: not started: ${error.message}`);
    }
  }
  if (thing !== 'all') assert.ok(failures.length === 0, failures.join('; '));
  const revision = await target();
  if (thing === 'all') {
    const selected = [];
    for (const [kind, name, deployed] of live) {
      if (!states.has(name)) continue;
      const alert = states.get(name);
      if (revision === null) {
        if (deployed === null) failures.push(`${name}: nothing qualified and no live revision`);
        report(`${name}: live ${deployed ?? 'absent'}, no qualified target`);
        continue;
      }
      try {
        const decision = work(kind, name, deployed, revision);
        const selectedJob = due(alert, deployed, revision, decision);
        if (selectedJob) selected.push([kind, name]);
        const attempt = deployed === revision ? null : next(alert, revision);
        const note = selectedJob ? `${decision}; locked job starts to reconcile state` : typeof attempt === 'string' ? attempt : decision;
        report(`${name}: live ${deployed ?? 'absent'}, target ${revision}: ${note}`);
      } catch (error) {
        failures.push(error.message);
        report(`${name}: not started: ${error.message}`);
      }
    }
    if (revision !== null) {
      const head = git('rev-parse', 'origin/main');
      for (const service of ['auth', 'dashboard']) {
        if (!selected.some(([kind]) => kind === service)) continue;
        if (head === revision) {
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
          continue;
        }
        const jobName = service === 'auth' ? 'main-auth' : 'main-dashboard / Publish and release';
        const { workflow_runs: runs } = await github(`actions/workflows/ci.yml/runs?head_sha=${revision}&branch=main&event=push&per_page=1`);
        const run = runs[0];
        assert.ok(run?.id, `${service}: no main-push CI run for ${revision}`);
        if (run.status !== 'completed') {
          report(`${service}: waiting for the target push run ${run.html_url}, status ${run.status}`);
          if (Date.now() - Date.parse(run.created_at) > activeLimit) failures.push(`${service}: ${run.html_url} has been active for more than two hours`);
          continue;
        }
        const { jobs, total_count: count } = await github(`actions/runs/${run.id}/jobs?filter=latest&per_page=100`);
        assert.ok(count <= 100, 'CI jobs exceed the qualification read bound');
        const job = jobs.find((entry) => entry.name === jobName);
        assert.ok(job?.id, `${service}: ${run.html_url} has no ${jobName} job to re-run`);
        await github(`actions/jobs/${job.id}/rerun`, 'POST');
        report(`${service}: re-ran ${jobName} on ${run.html_url}`);
      }
    }
    appendFileSync(process.env.GITHUB_OUTPUT, `docs=${selected.some(([kind]) => kind === 'docs')}\nlanding=${selected.some(([kind]) => kind === 'landing')}\nstatus=${JSON.stringify(selected.filter(([kind]) => kind === 'status').map(([, name]) => name))}\n`);
    console.log(`GitHub API reads: ${reads}`);
    assert.ok(failures.length === 0, failures.join('; '));
  } else {
    assert.ok(live.length === 1, 'A locked deploy job reconciles exactly one thing');
    const [[kind, name, deployed]] = live;
    const alert = states.get(name);
    let deploy = false;
    if (revision === null) {
      assert.ok(deployed !== null, `${name}: nothing qualified and no live revision`);
      report(`${name}: live ${deployed}, no qualified target`);
    } else {
      const decision = work(kind, name, deployed, revision);
      if (deployed === revision) {
        if (!healthy(alert, revision)) await writeState(alert, name, revision, 0, false);
        report(`${name}: live ${deployed}, target ${revision}: already live; state healthy`);
      } else if (services[kind] && revision !== process.env.GITHUB_SHA) {
        report(`${name}: live ${deployed ?? 'absent'}, target ${revision}: signed run ${process.env.GITHUB_SHA} cannot release it`);
      } else {
        const attempt = next(alert, revision);
        if (typeof attempt !== 'number') {
          if (attempt === 'suppressed after two attempts' && !alert.notified) await notify(alert, name, revision);
          report(`${name}: live ${deployed ?? 'absent'}, target ${revision}: ${attempt}`);
        } else if (decision !== 'deploy') {
          if (alert === null) await writeState(null, name, revision, 0, false);
          report(`${name}: live ${deployed}, target ${revision}: ${decision}`);
        } else {
          await writeState(alert, name, revision, attempt, false, true);
          deploy = true;
          report(`${name}: live ${deployed ?? 'absent'}, target ${revision}: deploying, attempt ${attempt} of 2 recorded`);
        }
      }
    }
    appendFileSync(process.env.GITHUB_OUTPUT, `deploy=${deploy}\n`);
    if (deploy) appendFileSync(process.env.GITHUB_ENV, `REVISION=${revision}\n`);
    console.log(`GitHub API reads: ${reads}`);
  }
}
