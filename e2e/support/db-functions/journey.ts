import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { resolve } from 'node:path';
import { expect } from '@playwright/test';
import { journey } from '../state';
import { test, home, project, name, json, removeDatabase } from '../../placement/support';

export function databaseFunctions(): void {
for (const region of ['gru', 'fra']) for (const surface of ['REST', 'WebSocket', 'Postgres']) {
test(`SQL functions through ${region} ${surface}`, async ({ cells }) => {
  const created = await json<{ id: string }>(await cells.post(home + project() + '/databases', {
    data: { name: name('db-functions'), regions: ['gru', 'fra'] },
  }), 201);
  let child: ReturnType<typeof spawn> | undefined;
  try {
    child = spawn(process.execPath, [resolve('support/db-functions/run.mjs'), 'local', region, surface], {
      stdio: ['pipe', 'pipe', 'pipe'],
    });
    let output = '';
    let errors = '';
    child.stdout!.on('data', chunk => { output += String(chunk); });
    child.stderr!.on('data', chunk => { errors += String(chunk); });
    const completed = once(child, 'exit');
    const current = journey();
    child.stdin!.end(JSON.stringify({ projectId: current.projectId, databaseId: created.id, apiKey: current.apiKey }));
    const [code] = await completed;
    console.log(output);
    expect(code, errors).toBe(0);
  } finally {
    if (child && child.exitCode === null && child.signalCode === null) {
      const closed = once(child, 'close');
      child.kill();
      await closed;
    }
    await removeDatabase(cells, created.id);
  }
});
}
}
