import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { readFileSync } from 'node:fs';
import { connect } from 'node:http2';
import { join } from 'node:path';
import { expect } from '@playwright/test';
import { localPort, stateDir } from '../support/stack';
import { journey, saveSecret } from '../support/state';

const internalOrigin = () => `http://127.0.0.1:${localPort('internal', 8081)}`;
const peerCredential = () => readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[0];

export async function internalRequest(method: string, path: string, payload?: unknown): Promise<{ status: number; body: string }> {
  const session = connect(internalOrigin());
  const stream = session.request({ ':method': method, ':path': path, 'x-terrabase-peer-credential': peerCredential(),
    ...(payload === undefined ? {} : { 'content-type': 'application/json' }) });
  session.on('error', error => stream.destroy(error));
  try {
    const response = once(stream, 'response');
    stream.end(payload === undefined ? undefined : JSON.stringify(payload));
    const [headers] = await response;
    let body = '';
    for await (const chunk of stream) body += chunk;
    return { status: Number(headers[':status']), body };
  } finally {
    session.close();
  }
}

export async function issueKey(organizationId: string, projectId: string, databaseId?: string): Promise<string> {
  const answer = await internalRequest('POST', '/admin/api-keys', { name: 'placement restriction', scopes: ['sql:read', 'sql:write'], organizationId, projectId, databaseId });
  expect(answer.status, 'issued a real scoped tb_ key').toBe(201);
  const secret = JSON.parse(answer.body).secret as string;
  expect(secret.startsWith('tb_')).toBe(true);
  saveSecret('placement-tb-key', secret);
  return secret;
}

export function postgres(id: string, token: string) {
  return spawn('psql', ['-X', '-tA', '-v', 'ON_ERROR_STOP=1', '-v', 'VERBOSITY=verbose',
    'postgresql://any@pg.terrascale.tech:5432/' + journey().projectId + '/' + id + '?sslmode=verify-full&sslrootcert=' + encodeURIComponent(join(stateDir, 'pki', 'ca.pem'))], {
    env: { PATH: process.env.PATH, SSL_CERT_FILE: process.env.SSL_CERT_FILE, PGPASSWORD: token,
      PGSSLCERT: join(stateDir, 'postgres-client.crt'), PGSSLKEY: join(stateDir, 'postgres-client.key') },
    stdio: ['pipe', 'pipe', 'pipe'],
  });
}
