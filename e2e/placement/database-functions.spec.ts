import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { readFileSync } from 'node:fs';
import { connect } from 'node:http2';
import { resolve, join } from 'node:path';
import { expect } from '@playwright/test';
import { test as base } from '../support/audit';
import { createDedicatedProjectOwner } from '../support/project-owner';
import { localPort, production, stateDir } from '../support/stack';
import { authenticatedCells, gru, project, name, json, removeDatabase, type Cells } from './support';

base.skip(production || process.env.E2E_REGIONS !== '2' || process.env.E2E_NODES_PER_REGION !== '1',
  'one node in each of two regions');

base.describe('database functions', () => {
  let owner: Awaited<ReturnType<typeof createDedicatedProjectOwner>>;
  const test = base.extend<{ cells: Cells }>({
    cells: async ({ playwright, request }, use) => {
      const current = owner.current;
      const cells = await authenticatedCells(playwright.request, request, current.apiKey, current.projectId);
      try { await use(cells); }
      finally { await cells.dispose(); }
    },
  });
  test.beforeAll(async ({ browser }, info) => {
    owner = await createDedicatedProjectOwner(browser, info, 'Database functions owner');
    await publish(owner.current.organizationId);
  });
  test.afterAll(async () => { if (owner) await owner.remove(); });
  for (const region of ['gru', 'fra']) for (const surface of ['REST', 'WebSocket', 'Postgres']) {
    test(`SQL functions through ${region} ${surface}`, async ({ cells }) => {
      const current = owner.current;
      const created = await json<{ id: string }>(await cells.post(gru + project(current.projectId) + '/databases', {
        data: { name: name('db-functions'), regions: ['gru', 'fra'] },
      }), 201);
      try {
        await run(current, 'run.mjs', created.id, region, surface);
      } finally {
        await removeDatabase(cells, created.id, current.projectId);
      }
    });
  }
  test('SQL functions through management, recovery and replication', async ({ cells }) => {
    const current = owner.current;
    const created = await json<{ id: string }>(await cells.post(gru + project(current.projectId) + '/databases', {
      data: { name: name('db-management'), regions: ['gru', 'fra'] },
    }), 201);
    try {
      await run(current, 'management.mjs', created.id);
    } finally {
      await removeDatabase(cells, created.id, current.projectId);
    }
  });
});

async function run(current: { projectId: string; apiKey: string }, script: string, databaseId: string, ...selection: string[]): Promise<void> {
  const child = spawn(process.execPath, [resolve('support/db-functions', script), 'local', ...selection], {
    stdio: ['pipe', 'pipe', 'pipe'],
  });
  try {
    let errors = '';
    child.stdout!.on('data', chunk => { process.stdout.write(String(chunk)); });
    child.stderr!.on('data', chunk => { errors += String(chunk); });
    const completed = once(child, 'exit');
    child.stdin!.end(JSON.stringify({ projectId: current.projectId, databaseId, apiKey: current.apiKey }));
    const [code] = await completed;
    expect(code, errors).toBe(0);
  } finally {
    if (child.exitCode === null && child.signalCode === null) {
      const closed = once(child, 'close');
      child.kill();
      await closed;
    }
  }
}

async function publish(organizationId: string): Promise<void> {
  const credential = readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[0];
  const session = connect('http://127.0.0.1:' + localPort('internal', 8081));
  const stream = session.request({
    ':method': 'PUT', ':path': `/admin/organizations/${organizationId}/plan`,
    'x-terrabase-peer-credential': credential, 'content-type': 'application/json',
  });
  session.on('error', error => stream.destroy(error));
  try {
    const response = once(stream, 'response');
    stream.end(JSON.stringify({ plan: 'pro' }));
    const [headers] = await response;
    for await (const chunk of stream) void chunk;
    expect(Number(headers[':status'])).toBe(200);
  } finally {
    session.close();
  }
}
