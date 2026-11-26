import assert from 'node:assert/strict';
import { appendFileSync, writeFileSync } from 'node:fs';
import { execFileSync, spawnSync } from 'node:child_process';

const [thing, ...args] = process.argv.slice(2);
const { GITHUB_REPOSITORY: repo, CLOUDFLARE_ACCOUNT_ID: account, CLOUDFLARE_API_TOKEN: token } = process.env;
const services = { dashboard: 'https://dash.terrascale.tech/readyz', auth: 'https://auth.terrascale.tech/readyz' };
const label = 'deployment-alert';
const bot = 'github-actions[bot]';
const workerProbeNotePrefix = 'Last failed Worker probe: ';
const runLink = `${process.env.GITHUB_SERVER_URL}/${repo}/actions/runs/${process.env.GITHUB_RUN_ID}`;
const activeLimit = 2 * 60 * 60 * 1000;
assert.ok(['all', 'docs', 'landing', 'status', 'terrabase', 'platform', ...Object.keys(services)].includes(thing));
let reads = 0;
const git = (...argv) => execFileSync('git', argv, { encoding: 'utf8' }).trim();
const sha = (value) => assert.match(value ?? '', /^[0-9a-f]{40}$/, 'Missing or invalid live revision');
const report = (line) => {
  console.log(line);
  appendFileSync(process.env.GITHUB_STEP_SUMMARY, `- ${line}\n`);
};

function request(path, method = 'GET', body) {
  if (method === 'GET') reads++;
  return fetch(`https://api.github.com/repos/${repo}/${path}`, {
    method,
    headers: { authorization: `Bearer ${process.env.GH_TOKEN}`, accept: 'application/vnd.github+json' },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(30000),
  });
}

async function github(path, method = 'GET', body) {
  const response = await request(path, method, body);
  const existingLabel = path === 'labels' && response.status === 422;
  assert.ok(response.ok || existingLabel, `GitHub ${method} ${path}: HTTP ${response.status}`);
  return response.status === 204 || existingLabel ? null : response.json();
}

async function probeJson(response, message) {
  try {
    return await response.json();
  } catch (error) {
    if (!(error instanceof SyntaxError)) throw error;
    assert.fail(message);
  }
}

async function cloudflare(path, signal) {
  const response = await fetch(`https://api.cloudflare.com/client/v4/accounts/${account}/workers/scripts/${path}`, {
    headers: { authorization: `Bearer ${token}` },
    signal,
  });
  const status = `Cloudflare ${path}: HTTP ${response.status}`;
  const body = await probeJson(response, `${status}, response was not JSON`);
  const codes = Array.isArray(body?.errors) ? body.errors.map((error) => error?.code).filter(Number.isSafeInteger) : [];
  if (response.status === 404 && codes.includes(10007) && path.endsWith('/deployments')) return null;
  const details = codes.length > 0 ? `, error codes ${codes.join(', ')}` : '';
  assert.ok(response.ok && body?.success === true, `${status}${details}`);
  assert.ok(body.result && typeof body.result === 'object', `Cloudflare ${path}: missing result`);
  return body.result;
}

async function serviceLive(kind, signal) {
  const response = await fetch(services[kind], { signal });
  assert.ok(response.ok, `readiness HTTP ${response.status}`);
  const live = response.headers.get('x-terrascale-revision');
  sha(live);
  const body = await probeJson(response, 'readiness response was not JSON');
  assert.ok(['ok', 'ready'].includes(body?.status), 'readiness status is unhealthy');
  return live;
}

async function workerLive(worker, signal) {
  const result = await cloudflare(`${worker}/deployments`, signal);
  if (result === null) return null;
  const versions = result.deployments?.[0]?.versions;
  assert.ok(Array.isArray(versions) && versions.length === 1 && versions[0]?.percentage === 100, `${worker}: missing or split live deployment`);
  const versionId = versions[0].version_id;
  assert.ok(typeof versionId === 'string' && versionId.length > 0, `${worker}: missing live version ID`);
  const version = await cloudflare(`${worker}/versions/${encodeURIComponent(versionId)}`, signal);
  const live = version.annotations?.['workers/message'];
  sha(live);
  return live;
}

async function probeLive(read, timeout) {
  const signal = AbortSignal.timeout(timeout);
  try {
    return { revision: await read(signal), failure: null };
  } catch (error) {
    const aborted = error instanceof DOMException && ['AbortError', 'TimeoutError'].includes(error.name);
    const transportFailure = error instanceof TypeError && error.cause && ['fetch failed', 'terminated'].includes(error.message);
    if (!aborted && !transportFailure && !(error instanceof assert.AssertionError)) throw error;
    if (signal.aborted) return { revision: null, failure: `probe timed out after ${timeout / 1000}s` };
    const code = error.cause?.code;
    const failure = code ? `${error.message} (${code})` : error.message;
    return { revision: null, failure };
  }
}

function ancestor(base, head) {
  const result = spawnSync('git', ['merge-base', '--is-ancestor', base, head]);
  assert.ok(result.status === 0 || result.status === 1, `Cannot compare ${base} to ${head}`);
  return result.status === 0;
}

function qualifiedJobs(jobs) {
  return jobs.some((job) => job.name === 'changes' && job.conclusion === 'success')
    && !jobs.some((job) => job.name === 'all-green' && job.conclusion === 'cancelled');
}

async function qualified(revision) {
  const { workflow_runs: runs } = await github(`actions/workflows/ci.yml/runs?head_sha=${revision}&branch=main&event=push&per_page=1`);
  const run = runs[0];
  if (!run) return false;
  assert.ok(run.head_sha === revision && run.head_branch === 'main' && run.event === 'push' && run.head_repository.full_name === repo, 'Unexpected CI identity');
  const { jobs, total_count: count } = await github(`actions/runs/${run.id}/jobs?filter=latest&per_page=100`);
  assert.ok(count <= 100, 'CI jobs exceed the qualification read bound');
  return qualifiedJobs(jobs);
}

async function target() {
  for (const revision of git('rev-list', '--first-parent', '-10', 'origin/main').split('\n')) {
    if (await qualified(revision)) return revision;
  }
  return null;
}

function inputs(thing) {
  const common = ['.github/scripts/ci/site-deploy-gate.mjs', ':(exclude,glob)**/AGENTS.md'];
  if (!services[thing]) return [...common, `sites/${thing}`, '.github/workflows/sites.yml', '.github/scripts/ci/prove-worker-deploy.mjs', ...(thing === 'docs' ? ['Makefile', 'sites/landing/static/pricing.json', 'sites/landing/static/regions.json'] : [])];
  const shared = ['Directory.*', 'global.json', 'NuGet.config', '.editorconfig', 'BannedSymbols.txt', 'Makefile', 'sdk/dotnet', ':(glob)src/TerraScale.Endpoints*/**', 'src/TerraScale.Contracts', '.github/scripts/ci/image-payloads.sh', '.github/scripts/ci/reconcile-image.sh', '.github/scripts/ci/inspect-app-image.sh', '.github/scripts/ci/deploy-v2.sh', '.github/scripts/ci/publish-ocir.sh', '.github/scripts/ci/publish-image.sh', '.github/scripts/ci/ocir-repositories.sh', '.github/scripts/ci/release-only.sh'];
  return [...common, ...shared, ...(thing === 'auth'
    ? [':(glob)src/TerraScale.Authentication*/**', 'src/TerraScale.Logging', '.github/docker/ts-auth.Dockerfile', '.github/scripts/auth', '.github/workflows/ci.yml']
    : [':(glob)src/TerraScale.Dashboard*/**', 'sites/landing/static/pricing.json', '.github/docker/dashboard.Dockerfile', '.github/scripts/dashboard', '.github/workflows/images-ghcr.yml'])];
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
    return (issue.title.startsWith('Deployment state:') && names.test(issue.title)) || blocks.some((block) => new RegExp(`"thing"\\s*:\\s*"${name}"`).test(block[1]))
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
  const probeNote = body.split('\n').find((line) => line.startsWith(workerProbeNotePrefix)) ?? null;
  return { issue, body, probeNote, ...state };
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

async function writeState(alert, name, revision, attempts, notified, { started = false, probeNote = null } = {}) {
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
    ...(probeNote === null ? [] : [probeNote]),
    '',
  ].join('\n');
  if (alert !== null) {
    await github(`issues/${alert.issue.number}`, 'PATCH', { body });
  } else {
    await github('labels', 'POST', { name: label, color: 'b60205' });
    await github('issues', 'POST', { title: `Deployment state: ${name}`, labels: [label], body });
  }
}

const workerProbeNote = (failure) => `${workerProbeNotePrefix}${failure}`;

async function recordWorkerProbeFailure(alert, name, failure) {
  const note = workerProbeNote(failure);
  if (alert === null) {
    const revision = await target();
    if (revision !== null) await writeState(null, name, revision, 0, false, { probeNote: note });
    return;
  }
  if (alert.probeNote === note) return;
  const lines = alert.body.trimEnd().split('\n');
  const body = [...lines.filter((line) => !line.startsWith(workerProbeNotePrefix)), note, ''].join('\n');
  await github(`issues/${alert.issue.number}`, 'PATCH', { body });
}

async function notify(alert, name, revision) {
  const runs = alert.body.split('\n').filter((line) => line.startsWith('Attempt '));
  await writeState(alert, name, revision, 2, true);
  await github(`issues/${alert.issue.number}/comments`, 'POST', {
    body: `${name}: deployment of ${revision} is suppressed after two attempts.\n\n${runs.join('\n') || 'Attempt run links are unavailable.'}\n\nLand a newer commit that passes main CI, or run Sites manually on main with reset_thing=${name} after fixing the failure. Keep this issue open.`,
  });
}

if (thing === 'auth' && args[0] === '--probe') {
  const { revision, failure } = await probeLive((signal) => serviceLive(thing, signal), 10000);
  if (failure !== null) report(`${thing}: live unhealthy: ${failure}`);
  const result = revision === null ? 'unavailable=true' : `revision=${revision}`;
  appendFileSync(process.env.GITHUB_OUTPUT, `${result}\n`);
} else if (args[0] === '--prove') {
  sha(process.env.REVISION);
  assert.equal(await serviceLive(thing, AbortSignal.timeout(30000)), process.env.REVISION, `${thing}: deployed revision mismatch`);
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
  const authReconcile = thing === 'auth' && args[0] === '--reconcile';
  if (thing === 'auth') assert.ok(authReconcile, 'Auth reconciliation requires a live probe result');
  const targets = thing === 'all'
    ? [['docs', 'ts-docs'], ['landing', 'ts-landing'], ['status', 'ts-status-vantage-sfo'], ['status', 'ts-status-vantage-gru'], ['status', 'ts-status'], ['auth', 'auth'], ['dashboard', 'dashboard']]
    : services[thing] ? [[thing, thing]] : args.map((worker) => [thing, worker]);
  const live = await Promise.all(targets.map(async ([kind, name]) => {
    if (authReconcile) {
      const revision = process.env.AUTH_LIVE_REVISION || null;
      if (revision !== null) sha(revision);
      return { kind, name, revision, failure: null };
    }
    const result = services[kind]
      ? await probeLive((signal) => serviceLive(kind, signal), 10000)
      : await probeLive((signal) => workerLive(name, signal), 30000);
    return { kind, name, ...result };
  }));
  assert.ok(live.length > 0, 'No deployment selected');
  const issues = await alerts();
  const states = new Map();
  const failures = [];
  for (const { name, revision, failure } of live) {
    if (failure !== null) {
      const message = `${name}: live unhealthy: ${failure}`;
      if (thing === 'all') failures.push(message);
      report(message);
    }
    try {
      assert.ok(revision === null || ancestor(revision, 'origin/main'), `${name}: live ${revision} is not on main`);
      states.set(name, alertFor(issues, name));
    } catch (error) {
      failures.push(error.message);
      report(`${name}: not started: ${error.message}`);
    }
  }
  if (thing !== 'all') assert.ok(failures.length === 0, failures.join('; '));
  if (thing === 'all') {
    const revision = await target();
    const head = git('rev-parse', 'origin/main');
    const selected = [];
    for (const { kind, name, revision: deployed, failure } of live) {
      if (!states.has(name)) continue;
      const alert = states.get(name);
      if (failure !== null && !services[kind]) {
        if (alert !== null ? alert.probeNote !== workerProbeNote(failure) : revision !== null) selected.push([kind, name]);
        continue;
      }
      const staleProbeNote = !services[kind] && alert !== null && alert.probeNote !== null;
      const liveStatus = failure === null ? deployed ?? 'absent' : 'unhealthy';
      if (revision === null) {
        if (staleProbeNote) selected.push([kind, name]);
        report(`${name}: live ${liveStatus}, no qualified target`);
        continue;
      }
      if (services[kind] && revision !== head) {
        report(`${name}: live ${liveStatus}, target ${revision}: waiting for main ${head} to qualify; a dispatched release signs main's head`);
        continue;
      }
      try {
        const decision = work(kind, name, deployed, revision);
        const selectedJob = staleProbeNote || due(alert, deployed, revision, decision);
        if (selectedJob) selected.push([kind, name]);
        const attempt = deployed === revision ? null : next(alert, revision);
        const note = selectedJob ? `${decision}; locked job starts to reconcile state` : typeof attempt === 'string' ? attempt : decision;
        report(`${name}: live ${liveStatus}, target ${revision}: ${note}`);
      } catch (error) {
        failures.push(error.message);
        report(`${name}: not started: ${error.message}`);
      }
    }
    appendFileSync(process.env.GITHUB_OUTPUT, `docs=${selected.some(([kind]) => kind === 'docs')}\nlanding=${selected.some(([kind]) => kind === 'landing')}\nstatus=${JSON.stringify(selected.filter(([kind]) => kind === 'status').map(([, name]) => name))}\n`);
    if (revision !== null) {
      for (const service of ['auth', 'dashboard']) {
        if (!selected.some(([kind]) => kind === service)) continue;
        const workflow = service === 'auth' ? 'ci.yml' : 'images-ghcr.yml';
        const active = [];
        for (const status of ['queued', 'in_progress', 'waiting', 'pending', 'requested']) {
          const { workflow_runs: runs } = await github(`actions/workflows/${workflow}/runs?branch=main&status=${status}&per_page=100`);
          active.push(...runs);
        }
        if (active.length === 0) {
          await github(`actions/workflows/${workflow}/dispatches`, 'POST', { ref: 'main', inputs: service === 'auth' ? { reconcile_auth: true } : {} });
          report(`${service}: requested its locked deploy job in ${workflow}`);
          continue;
        }
        const oldest = active.toSorted((left, right) => Date.parse(left.created_at) - Date.parse(right.created_at))[0];
        report(`${service}: waiting for the active ${workflow} run ${oldest.html_url}, active since ${oldest.created_at}`);
        if (Date.now() - Date.parse(oldest.created_at) > activeLimit) failures.push(`${service}: ${oldest.html_url} has been active for more than two hours`);
      }
    }
    console.log(`GitHub API reads: ${reads}`);
    assert.ok(failures.length === 0, failures.join('; '));
  } else {
    assert.ok(live.length === 1, 'A locked deploy job reconciles exactly one thing');
    const [{ kind, name, revision: deployed, failure }] = live;
    const alert = states.get(name);
    const own = process.env.GITHUB_SHA;
    assert.match(own ?? '', /^[0-9a-f]{40}$/, 'Missing or invalid run commit');
    const workerUnavailable = failure !== null && !services[kind];
    const ownQualified = !workerUnavailable && (services[kind] ? ancestor(own, 'origin/main') : await qualified(own));
    const revision = ownQualified ? own : null;
    let deploy = false;
    let clearProbeNote = !services[kind] && !workerUnavailable && alert !== null && alert.probeNote !== null;
    const liveStatus = failure === null ? deployed ?? 'absent' : 'unhealthy';
    if (workerUnavailable) {
      await recordWorkerProbeFailure(alert, name, failure);
    } else if (revision === null) {
      report(`${name}: live ${liveStatus}, this run's commit ${own} is not qualified; a release ships only that commit`);
    } else {
      const decision = work(kind, name, deployed, revision);
      if (deployed === revision) {
        if (!healthy(alert, revision)) {
          await writeState(alert, name, revision, 0, false);
          clearProbeNote = false;
        }
        report(`${name}: live ${deployed}, target ${revision}: already live; state healthy`);
      } else {
        const attempt = next(alert, revision);
        if (typeof attempt !== 'number') {
          if (attempt === 'suppressed after two attempts' && !alert.notified) {
            await notify(alert, name, revision);
            clearProbeNote = false;
          }
          report(`${name}: live ${liveStatus}, target ${revision}: ${attempt}`);
        } else if (decision !== 'deploy') {
          if (alert === null) await writeState(null, name, revision, 0, false);
          report(`${name}: live ${deployed}, target ${revision}: ${decision}`);
        } else {
          await writeState(alert, name, revision, attempt, false, { started: true });
          deploy = true;
          report(`${name}: live ${liveStatus}, target ${revision}: deploying, attempt ${attempt} of 2 recorded`);
        }
      }
    }
    if (!deploy && clearProbeNote) {
      const body = alert.issue.body.split('\n').filter((line) => !line.startsWith(workerProbeNotePrefix)).join('\n');
      await github(`issues/${alert.issue.number}`, 'PATCH', { body });
      report(`${name}: cleared the live probe failure after recovery`);
    }
    appendFileSync(process.env.GITHUB_OUTPUT, `deploy=${deploy}\n`);
    if (deploy) appendFileSync(process.env.GITHUB_ENV, `REVISION=${revision}\n`);
    console.log(`GitHub API reads: ${reads}`);
    assert.ok(!workerUnavailable, `${name}: live unhealthy: ${failure}`);
  }
}
