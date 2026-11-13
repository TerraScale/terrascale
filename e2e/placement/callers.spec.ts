import { execFile } from 'node:child_process';
import { join } from 'node:path';
import { promisify } from 'node:util';
import { expect } from '@playwright/test';
import { auth, e2eRoot, production, stateDir } from '../support/stack';
import { journey, saveSecret } from '../support/state';
import { test, replica, project, name, json, sql, removeDatabase, type Database } from './support';

test('bearer RPC calls preserve regional forwarding and usage with caller assertions', async ({ cells, request }) => {
  test.skip(production || process.env.E2E_REGIONS !== '2', 'requires the local two-region stack');
  const current = journey();
  const created = await json<Database>(await cells.post(replica + project() + '/databases', {
    data: { name: name('caller-bridge'), homeRegion: 'fra', replicaRegions: [] },
  }), 201);
  try {
    await sql(cells, replica, created.id, 'CREATE TABLE caller_bridge_rows (value INTEGER PRIMARY KEY)');
    await sql(cells, replica, created.id, 'INSERT INTO caller_bridge_rows VALUES (291)');
    const response = await request.post(auth + '/terrascale/oauth/token', {
      form: {
        grant_type: 'client_credentials', client_id: current.apiKey.slice(0, 26), client_secret: current.apiKey,
        project: current.projectId, scope: 'sql:read sql:write databases:read databases:write',
      },
    });
    expect(response.status()).toBe(200);
    const grant = await response.json() as { access_token: string };
    saveSecret('caller-bridge-token', grant.access_token);
    const { stdout } = await promisify(execFile)(process.execPath,
      [join(e2eRoot, 'env/caller-bridge-probe.mjs'), stateDir, current.projectId, created.id],
      { env: { ...process.env, E2E_CALLER_BEARER: 'Bearer ' + grant.access_token } });
    expect(stdout).toContain('bridge bearer caller=false: fra rows=[[291]], fra metered POST +1');
    expect(stdout).toContain('bridge bearer caller=true: fra rows=[[291]], fra metered POST +1');
    expect(stdout).toContain('forwarded public caller header: fra rows=[[291]], rejected caller +0');
    console.log(stdout);
  } catch (error) {
    console.log('regional caller primary failure:', error);
    throw error;
  } finally {
    await removeDatabase(cells, created.id);
  }
});
