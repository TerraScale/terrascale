import { createHmac, randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { readFileSync } from 'node:fs';
import { connect, constants } from 'node:http2';
import { join } from 'node:path';
import { expect, test } from '../support/audit';
import { functionAccess } from '../support/functions';
import { onLocalStack, stateDir } from '../support/stack';
import { journey, saveSecret } from '../support/state';

test('an aborted function invocation is logged as client closed without an invocation error', async ({ request }) => {
  await onLocalStack(async () => {
    const current = journey();
    const headers = await functionAccess(request);
    const path = `/v1/projects/${current.projectId}/databases/${current.databaseId}`;
    const endpoint = 'https://api.terrascale.tech' + path;
    const requestId = '01' + randomBytes(12).toString('hex').toUpperCase();
    const table = 'abort_' + requestId.toLowerCase();
    const name = 'abort-' + requestId.toLowerCase();
    const execute = async (sql: string) => {
      const response = await request.post(endpoint + '/sql/execute', { headers, data: { statements: [{ sql }] } });
      expect(response.status()).toBe(200);
      return response.json() as Promise<{ results: { rows: unknown[][] }[] }>;
    };
    await execute(`CREATE TABLE ${table} (id TEXT PRIMARY KEY)`);
    const deployed = await request.put(endpoint + '/functions/' + name, {
      headers, data: { source: `async function handler() {
        await sql.execute("INSERT INTO ${table} VALUES ('started')");
        return await new Promise(() => {});
      }` },
    });
    expect(deployed.status()).toBe(200);
    const secret = readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[0];
    const proof = createHmac('sha256', secret).update('terrascale.request-id.v1\n' + requestId).digest('hex').toUpperCase();
    saveSecret('request-abort-proof', proof);
    const session = connect('https://api.terrascale.tech', { ca: readFileSync(join(stateDir, 'pki', 'ca.pem')) });
    try {
      const stream = session.request({
        ':method': 'POST', ':path': path + '/functions/' + name + '/invoke', authorization: headers.Authorization,
        'content-type': 'application/json',
        'x-request-id': requestId, 'x-terrascale-request-proof': proof,
      });
      stream.end('{}');
      await expect.poll(async () => (await execute(`SELECT id FROM ${table}`)).results[0].rows).toEqual([['started']]);
      const closed = once(stream, 'close');
      stream.close(constants.NGHTTP2_CANCEL);
      await closed;
      const recorded = async (status: string) => {
        const response = await request.get(endpoint + '/logs', {
          headers, params: { status },
        });
        expect(response.status()).toBe(200);
        const body = await response.json() as { items: { requestId: string; status: number; method: string; route: string }[] };
        return body.items.filter(item => item.requestId === requestId);
      };
      await expect.poll(async () => (await recorded('')).map(item => item.status)).toEqual([499]);
      expect((await recorded('4xx')).map(item => item.status)).toEqual([499]);
      expect(await recorded('5xx')).toEqual([]);
      const logs = await request.get(endpoint + '/functions/' + name + '/logs', { headers });
      expect(logs.status()).toBe(200);
      expect(await logs.json()).toEqual([]);
      const stats = await request.get(endpoint + '/functions/' + name + '/stats', { headers });
      expect(stats.status()).toBe(200);
      expect(await stats.json()).toMatchObject({ invocations: 0, errors: 0 });
    } finally {
      session.destroy();
      expect((await request.delete(endpoint + '/functions/' + name, { headers })).status()).toBe(204);
      await execute(`DROP TABLE ${table}`);
    }
  });
});
