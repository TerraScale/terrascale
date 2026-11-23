import assert from 'node:assert/strict';
import { appendFileSync, readFileSync } from 'node:fs';

const [record, ...workers] = process.argv.slice(2);
const { CLOUDFLARE_ACCOUNT_ID: account, CLOUDFLARE_API_TOKEN: token, REVISION: revision } = process.env;
assert.match(revision ?? '', /^[0-9a-f]{40}$/, 'REVISION must be the commit the job checked out');

const deploys = readFileSync(record, 'utf8').trim().split('\n').map((line) => JSON.parse(line)).filter((entry) => entry.type === 'deploy');
const deployed = deploys.map((entry) => entry.worker_name);
assert.deepEqual(deployed.toSorted(), workers.toSorted(), `The deploy recorded ${deployed.join(', ') || 'no Worker'}, expected ${workers.join(', ')}`);

async function cloudflare(path) {
  const response = await fetch(`https://api.cloudflare.com/client/v4/accounts/${account}/workers/scripts/${path}`, {
    headers: { authorization: `Bearer ${token}` },
  });
  const body = await response.json();
  assert.ok(body.success, `GET ${path}: HTTP ${response.status} ${JSON.stringify(body.errors)}`);
  return body.result;
}

for (const { worker_name: worker, version_id: version } of deploys) {
  const [active] = (await cloudflare(`${worker}/deployments`)).deployments;
  const traffic = active.versions.map((entry) => [entry.version_id, entry.percentage]);
  assert.deepEqual(traffic, [[version, 100]], `${worker}: the active deployment ${active.id} serves ${JSON.stringify(traffic)}, not version ${version} at 100%`);
  const { annotations } = await cloudflare(`${worker}/versions/${version}`);
  assert.equal(annotations['workers/message'], revision, `${worker}: version ${version} does not carry ${revision}`);
  console.log(`${worker}: active deployment ${active.id} serves version ${version} at 100%, message ${revision}`);
  appendFileSync(process.env.GITHUB_STEP_SUMMARY, `- ${worker}: deployed ${revision}\n`);
}
