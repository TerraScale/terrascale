import assert from 'node:assert/strict';
import { appendFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

const label = 'deployment-alert';
const title = (thing) => `Deployment failure: ${thing}`;
export let reads = 0;

export async function github(path, method = 'GET', body) {
  if (method === 'GET') reads++;
  const response = await fetch(`https://api.github.com/repos/${process.env.GITHUB_REPOSITORY}/${path}`, {
    method,
    headers: { authorization: `Bearer ${process.env.GH_TOKEN}`, accept: 'application/vnd.github+json' },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(30000),
  });
  assert.ok(response.ok, `GitHub ${method} ${path}: HTTP ${response.status}`);
  return response.status === 204 ? null : response.json();
}

export async function alerts() {
  const issues = [];
  for (let page = 1; ; page++) {
    const batch = await github(`issues?state=open&labels=${label}&per_page=100&page=${page}`);
    issues.push(...batch.filter((issue) => !issue.pull_request));
    if (batch.length < 100) return issues;
  }
}

export function alertFor(issues, thing) {
  const matches = issues.filter((issue) => issue.title === title(thing));
  assert.ok(matches.length <= 1, `${thing}: duplicate deployment alerts`);
  return matches[0];
}

export function attempts(issue, revision) {
  if (!issue) return 0;
  const target = issue.body?.match(/^Target: ([0-9a-f]{40})$/m)?.[1];
  const count = issue.body?.match(/^Failed attempts: ([0-9]+)$/m)?.[1];
  assert.ok(target && count, `Issue ${issue.number}: invalid deployment alert state`);
  return target === revision ? Number(count) : 0;
}

export async function closeAlert(issue, revision) {
  if (!issue) return;
  await github(`issues/${issue.number}`, 'PATCH', {
    state: 'closed',
    body: `${issue.body}\n\nDeployed commit: ${revision}\n`,
  });
  console.log(`Closed deployment alert ${issue.number}; deployed ${revision}`);
}

async function record(thing, result, revision) {
  assert.match(revision ?? '', /^[0-9a-f]{40}$/);
  const issue = alertFor(await alerts(), thing);
  if (result === 'guard') {
    const deploy = attempts(issue, revision) < 2;
    appendFileSync(process.env.GITHUB_OUTPUT, `deploy=${deploy}\n`);
    console.log(deploy ? `${thing}: attempt allowed` : `${thing}: waiting for a new target or a closed issue`);
  } else if (result === 'success') {
    await closeAlert(issue, revision);
  } else {
    assert.ok(['failure', 'cancelled'].includes(result), 'Unexpected deployment outcome');
    const body = `Thing: ${thing}\nTarget: ${revision}\nFailed attempts: ${attempts(issue, revision) + 1}\nRun: ${process.env.GITHUB_SERVER_URL}/${process.env.GITHUB_REPOSITORY}/actions/runs/${process.env.GITHUB_RUN_ID}\n`;
    if (issue) {
      await github(`issues/${issue.number}`, 'PATCH', { body });
    } else {
      const labels = await github('labels?per_page=100');
      if (!labels.some((entry) => entry.name === label)) {
        const response = await fetch(`https://api.github.com/repos/${process.env.GITHUB_REPOSITORY}/labels`, {
          method: 'POST',
          headers: { authorization: `Bearer ${process.env.GH_TOKEN}`, accept: 'application/vnd.github+json' },
          body: JSON.stringify({ name: label, color: 'b60205' }),
          signal: AbortSignal.timeout(30000),
        });
        assert.ok(response.ok || response.status === 422, `Create alert label: HTTP ${response.status}`);
      }
      await github('issues', 'POST', { title: title(thing), labels: [label], body });
    }
    console.log(`${thing}: recorded failed deployment of ${revision}`);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  await record(...process.argv.slice(2));
}
