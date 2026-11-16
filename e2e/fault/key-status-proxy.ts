import { once } from 'node:events';
import { readFileSync } from 'node:fs';
import { createServer as httpServer } from 'node:http';
import { createServer as httpsServer, request } from 'node:https';
import { connect } from 'node:net';
import { join } from 'node:path';
import type { Duplex } from 'node:stream';
import { stateDir } from '../support/stack';

export async function keyStatusProxy() {
  let armed = false;
  let release: (() => void) | undefined;
  let held: (() => void) | undefined;
  const tunnels = new Set<Duplex>();
  const tls = httpsServer({ key: readFileSync(join(stateDir, 'pki/edge.key')), cert: readFileSync(join(stateDir, 'pki/edge.pem')) }, (incoming, outgoing) => {
    const upstream = request(`https://auth.terrascale.tech${incoming.url}`, {
      method: incoming.method, headers: { ...incoming.headers, host: 'auth.terrascale.tech' },
    }, response => {
      const chunks: Buffer[] = [];
      response.on('data', chunk => chunks.push(Buffer.from(chunk)));
      response.on('end', () => {
        const send = () => {
          outgoing.writeHead(response.statusCode!, response.headers);
          outgoing.end(Buffer.concat(chunks));
        };
        if (armed && incoming.url === '/v1/terrascale/api-keys/self' && response.statusCode === 200) {
          armed = false;
          release = send;
          held!();
        } else send();
      });
    });
    upstream.on('error', () => outgoing.destroy());
    incoming.pipe(upstream);
  });
  const proxy = httpServer();
  proxy.on('connect', (incoming, socket, head) => {
    tunnels.add(socket);
    socket.on('close', () => tunnels.delete(socket));
    if (incoming.url === 'auth.terrascale.tech:443') {
      socket.write('HTTP/1.1 200 Connection Established\r\n\r\n');
      if (head.length) socket.unshift(head);
      tls.emit('connection', socket);
    } else {
      const [host, port] = incoming.url!.split(':');
      const upstream = connect(Number(port), host, () => {
        socket.write('HTTP/1.1 200 Connection Established\r\n\r\n');
        if (head.length) upstream.write(head);
        socket.pipe(upstream).pipe(socket);
      });
      upstream.on('error', () => socket.destroy());
    }
  });
  proxy.listen(0, '127.0.0.1');
  await once(proxy, 'listening');
  const address = proxy.address() as { port: number };
  return {
    environment: { HTTPS_PROXY: `http://127.0.0.1:${address.port}`, https_proxy: `http://127.0.0.1:${address.port}`, NO_PROXY: '', no_proxy: '' },
    arm: () => {
      armed = true;
      return new Promise<void>(resolve => { held = resolve; });
    },
    release: () => { release!(); release = undefined; },
    close: async () => {
      release?.();
      for (const tunnel of tunnels) tunnel.destroy();
      proxy.closeAllConnections();
      await new Promise<void>(resolve => proxy.close(() => resolve()));
      tls.closeAllConnections();
      tls.close();
    },
  };
}
