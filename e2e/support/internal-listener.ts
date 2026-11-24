import { readFileSync } from 'node:fs';
import { connect } from 'node:http2';
import { join } from 'node:path';
import { localPort, stateDir } from './stack';
import { saveSecret } from './state';

export type CallerGrant = { endUser: boolean; anonymous: boolean; projectId: string; scopes: string[] };

export async function internalAuthorize(bearer: string, project: string): Promise<{ status: number; grant: CallerGrant }> {
  const credential = readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[0];
  saveSecret('internal-authorize-peer', credential);
  const session = connect(`http://127.0.0.1:${localPort('internal', 8081)}`);
  try {
    return await new Promise((resolve, reject) => {
      const stream = session.request({
        ':method': 'POST', ':path': '/v1/internal/authorize', 'content-type': 'application/json',
        'x-terrabase-peer-credential': credential, authorization: 'Bearer ' + bearer,
      });
      let status = 0;
      let body = '';
      stream.setEncoding('utf8');
      stream.on('response', headers => { status = Number(headers[':status']); });
      stream.on('data', chunk => { body += chunk; });
      stream.on('error', reject);
      session.on('error', reject);
      stream.on('end', () => resolve({ status, grant: JSON.parse(body) as CallerGrant }));
      stream.end(JSON.stringify({ project, database: null, scopes: ['sql:read'] }));
    });
  } finally {
    session.close();
  }
}
