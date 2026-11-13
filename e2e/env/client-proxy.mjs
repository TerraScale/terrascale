import { createServer, request as forward } from 'node:http';
import { connect } from 'node:net';
import { readFileSync } from 'node:fs';

const hosts = new Set(readFileSync(new URL('./hostnames', import.meta.url), 'utf8').trim().split('\n'));
const allowed = (host) => hosts.has(host) || /^127\.\d+\.\d+\.\d+$/.test(host);
const server = createServer((request, response) => {
  const target = new URL(request.url);
  if (target.protocol !== 'http:' || !allowed(target.hostname)) {
    response.writeHead(403).end();
    return;
  }
  const upstream = forward(target, {
    method: request.method,
    headers: request.headers,
    localAddress: request.socket.localAddress,
  }, (reply) => {
    response.writeHead(reply.statusCode, reply.headers);
    reply.pipe(response);
  });
  upstream.on('error', () => { response.writeHead(502).end(); });
  request.on('error', () => upstream.destroy());
  request.pipe(upstream);
});
server.on('connect', (request, socket, head) => {
  const target = new URL(`https://${request.url}`);
  const port = Number(target.port || 443);
  if (!allowed(target.hostname) || (!target.hostname.startsWith('127.') && port !== 443)) {
    socket.end('HTTP/1.1 403 Forbidden\r\n\r\n');
    return;
  }
  const upstream = connect({ host: target.hostname, port, localAddress: socket.localAddress });
  upstream.on('connect', () => {
    socket.write('HTTP/1.1 200 Connection Established\r\n\r\n');
    upstream.write(head);
    upstream.pipe(socket);
    socket.pipe(upstream);
  });
  upstream.on('error', () => socket.destroy());
  socket.on('error', () => upstream.destroy());
  socket.on('close', () => upstream.destroy());
});
server.listen(8089, '0.0.0.0');
