import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { readFileSync } from 'node:fs';
import { connect } from 'node:http2';
import { resolve, join } from 'node:path';
import { expect } from '@playwright/test';
import { journey } from '../state';
import { localPort, stateDir } from '../stack';
import { test, home, project, name, json, removeDatabase } from '../../placement/support';

export function databaseFunctions(): void {
  for (const region of ['gru', 'fra']) for (const surface of ['REST', 'WebSocket', 'Postgres']) {
    test(`SQL functions through ${region} ${surface}`, async ({ cells }) => {
      const created = await json<{ id: string }>(await cells.post(home + project() + '/databases', {
        data: { name: name('db-functions'), regions: ['gru', 'fra'] },
      }), 201);
      try {
        await run('run.mjs', created.id, region, surface);
      } finally {
        await removeDatabase(cells, created.id);
      }
    });
  }
  test('SQL functions through management, recovery and replication', async ({ cells }) => {
    await publish('pro');
    const created = await json<{ id: string }>(await cells.post(home + project() + '/databases', {
      data: { name: name('db-management'), regions: ['gru', 'fra'] },
    }), 201);
    try {
      await run('management.mjs', created.id);
    } finally {
      await removeDatabase(cells, created.id);
      await publish('hobby');
    }
  });
}

async function run(script: string, databaseId: string, ...selection: string[]): Promise<void> {
  const child = spawn(process.execPath, [resolve('support/db-functions', script), 'local', ...selection], {
    stdio: ['pipe', 'pipe', 'pipe'],
  });
  try {
    let errors = '';
    child.stdout!.on('data', chunk => { process.stdout.write(String(chunk)); });
    child.stderr!.on('data', chunk => { errors += String(chunk); });
    const completed = once(child, 'exit');
    const current = journey();
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

async function publish(plan: string): Promise<void> {
  const credential = readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[0];
  const session = connect('http://127.0.0.1:' + localPort('internal', 8081));
  const stream = session.request({
    ':method': 'PUT', ':path': `/admin/organizations/${journey().organizationId}/plan`,
    'x-terrabase-peer-credential': credential, 'content-type': 'application/json',
  });
  session.on('error', error => stream.destroy(error));
  try {
    const response = once(stream, 'response');
    stream.end(JSON.stringify({ plan }));
    const [headers] = await response;
    for await (const chunk of stream) void chunk;
    expect(Number(headers[':status'])).toBe(200);
  } finally {
    session.close();
  }
}
