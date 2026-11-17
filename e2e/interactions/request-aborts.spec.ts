import { createHmac, randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { readFileSync } from 'node:fs';
import { connect, constants } from 'node:http2';
import { join } from 'node:path';
import { expect, test } from '../support/audit';
import { functionAccess } from '../support/functions';
import { onLocalStack, stateDir } from '../support/stack';
import { journey, saveSecret } from '../support/state';

test('an aborted SQL upload is logged as client closed instead of a server error', async ({ request }) => {
  await onLocalStack(async () => {
    const current = journey();
    const headers = await functionAccess(request);
    const path = `/v1/projects/${current.projectId}/databases/${current.databaseId}`;
    const requestId = '01' + randomBytes(12).toString('hex').toUpperCase();
    const secret = readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[0];
    const proof = createHmac('sha256', secret).update('terrascale.request-id.v1\n' + requestId).digest('hex').toUpperCase();
    saveSecret('request-abort-proof', proof);
    const session = connect('https://api.terrascale.tech', { ca: readFileSync(join(stateDir, 'pki', 'ca.pem')) });
    try {
      const stream = session.request({
        ':method': 'POST', ':path': path + '/sql/execute', authorization: headers.Authorization,
        'content-type': 'application/json', expect: '100-continue',
        'x-request-id': requestId, 'x-terrascale-request-proof': proof,
      });
      const continuing = once(stream, 'headers');
      stream.write('{"statements":[');
      expect((await continuing)[0][':status']).toBe(100);
      const closed = once(stream, 'close');
      stream.close(constants.NGHTTP2_CANCEL);
      await closed;
      const recorded = async (status: string) => {
        const response = await request.get('https://api.terrascale.tech' + path + '/logs', {
          headers, params: { status },
        });
        expect(response.status()).toBe(200);
        const body = await response.json() as { items: { requestId: string; status: number; method: string; route: string }[] };
        return body.items.filter(item => item.requestId === requestId);
      };
      await expect.poll(async () => (await recorded('4xx')).map(item => item.status)).toEqual([499]);
      expect(await recorded('5xx')).toEqual([]);
    } finally {
      session.destroy();
    }
  });
});
