export function rpc(session, path, headers, frame = Buffer.from([0, 0, 0, 0, 1, 0xc0])) {
  return new Promise((resolve, reject) => {
    const call = session.request({
      ':method': 'POST', ':path': path,
      'content-type': 'application/grpc', te: 'trailers', ...headers,
    });
    let status;
    let grpc;
    const chunks = [];
    call.on('response', headers => { status = headers[':status']; grpc = headers['grpc-status']; });
    call.on('trailers', headers => { grpc = headers['grpc-status']; });
    call.on('data', chunk => { chunks.push(chunk); });
    call.on('error', reject);
    call.on('end', () => resolve({ status, grpc, body: Buffer.concat(chunks) }));
    call.end(frame);
  });
}
