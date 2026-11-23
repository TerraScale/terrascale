export function h2(session, path, headers, body = null) {
  return new Promise((resolve, reject) => {
    const call = session.request({
      ':method': body === null ? 'GET' : 'POST', ':path': path,
      ...(body === null ? {} : { 'content-type': 'application/json' }), ...headers,
    });
    let status;
    let text = '';
    call.setEncoding('utf8');
    call.on('response', response => { status = response[':status']; });
    call.on('data', chunk => { text += chunk; });
    call.on('error', reject);
    call.on('end', () => resolve({ status, text }));
    call.end(body === null ? undefined : body);
  });
}
