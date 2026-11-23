import { execFile } from 'node:child_process';
import { join } from 'node:path';
import { promisify } from 'node:util';
import { expect } from '@playwright/test';
import { auth, e2eRoot, production, stateDir } from '../support/stack';
import { keyId } from '../support/api-key';
import { journey, saveSecret } from '../support/state';
import { test, gru, project, name, json, sql, removeDatabase, type Database } from './support';

test('bearer RPC calls on gru meter usage once with caller assertions and fra serves the same rows', async ({ cells, request }) => {
  test.skip(production || process.env.E2E_REGIONS !== '2', 'requires the local two-region stack');
  const current = journey();
  const created = await json<Database>(await cells.post(gru + project() + '/databases', {
    data: { name: name('caller-bridge'), regions: ['gru', 'fra'] },
  }), 201);
  try {
    await sql(cells, gru, created.id, 'CREATE TABLE caller_bridge_rows (value INTEGER PRIMARY KEY)');
    await sql(cells, gru, created.id, 'INSERT INTO caller_bridge_rows VALUES (291)');
    const response = await request.post(auth + '/terrascale/oauth/token', {
      form: {
        grant_type: 'client_credentials', client_id: keyId(current.apiKey), client_secret: current.apiKey,
        project: current.projectId, scope: 'sql:read sql:write databases:read databases:write',
      },
    });
    expect(response.status()).toBe(200);
    const grant = await response.json() as { access_token: string };
    saveSecret('caller-bridge-token', grant.access_token);
    const { stdout } = await promisify(execFile)(process.execPath,
      [join(e2eRoot, 'env/caller-bridge-probe.mjs'), stateDir, current.projectId, created.id],
      { env: { ...process.env, E2E_CALLER_BEARER: 'Bearer ' + grant.access_token } });
    expect(stdout).toContain('bridge bearer caller=false: gru rows=[[291]], gru metered requests +1, outer authentication recorded, rejected caller +0');
    expect(stdout).toContain('bridge bearer caller=true: gru rows=[[291]], gru metered requests +1, outer authentication recorded, rejected caller +0');
    expect(stdout).toContain('public caller header on fra: fra rows=[[291]], rejected caller +0');
    console.log(stdout);
  } catch (error) {
    console.log('caller bridge failure:', error);
    throw error;
  } finally {
    await removeDatabase(cells, created.id);
  }
});
