import assert from 'node:assert/strict';
import { connect } from 'node:http2';

export function internal(peer, address, method, path, body, actAs = true) {
  return new Promise((resolve, reject) => {
    const session = connect(address);
    session.on('error', reject);
    const stream = session.request({ ':method': method, ':path': path, 'content-type': 'application/json',
      'x-terrabase-peer-credential': peer,
      ...(actAs ? { 'x-terrabase-user-id': '01J00000000000000000000000' } : {}),
    });
    let status = 0;
    let text = '';
    stream.setEncoding('utf8');
    stream.on('response', headers => { status = Number(headers[':status']); });
    stream.on('data', chunk => { text += chunk; });
    stream.on('error', error => { session.close(); reject(error); });
    stream.on('end', () => {
      session.close();
      const answer = text ? JSON.parse(text) : null;
      if (![200, 201, 204].includes(status)) {
        reject(new Error(method + ' ' + path + ': ' + status + ' ' + answer?.error?.code));
      } else resolve(answer);
    });
    stream.end(body ? JSON.stringify(body) : undefined);
  });
}

export async function project(peer, address, name) {
  const organization = await internal(peer, address, 'POST', '/v1/management/organizations', { name });
  const created = await internal(peer, address, 'POST', '/v1/management/organizations/' + organization.id + '/projects', { name });
  assert.ok(organization.id && created.id, 'organization and project created');
  await internal(peer, address, 'PUT', '/admin/projects/' + created.id + '/enrollment', { organization: organization.id }, false);
  return { organization: organization.id, project: created.id };
}
