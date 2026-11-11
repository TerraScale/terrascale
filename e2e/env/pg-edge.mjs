import { readFileSync } from 'node:fs';
import { connect, createServer } from 'node:net';
import { TLSSocket } from 'node:tls';

const credentials = { key: readFileSync(process.env.E2E_EDGE_KEY), cert: readFileSync(process.env.E2E_EDGE_CERT) };
const sslRequest = Buffer.from([0, 0, 0, 8, 4, 210, 22, 47]);

createServer((client) => {
  let buffered = Buffer.alloc(0);
  client.on('error', () => client.destroy());
  client.on('data', function negotiate(chunk) {
    buffered = Buffer.concat([buffered, chunk]);
    if (buffered.length < sslRequest.length) return;
    client.off('data', negotiate);
    if (!buffered.equals(sslRequest)) return client.destroy();
    client.write('S');
    const secure = new TLSSocket(client, { isServer: true, ...credentials, ALPNProtocols: ['postgresql'] });
    secure.once('secure', () => {
      const terrabase = connect(6432, '127.0.0.1');
      secure.pipe(terrabase).pipe(secure);
      terrabase.on('error', () => secure.destroy());
      secure.on('close', () => terrabase.destroy());
    });
    secure.on('error', () => secure.destroy());
  });
}).listen(5432, '127.0.0.1');
