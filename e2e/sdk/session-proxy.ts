import { createServer } from 'node:http';
import { request } from 'node:https';
import type { Duplex } from 'node:stream';

function frames(consume: (body: Record<string, unknown>) => boolean): (chunk: Buffer) => Buffer[] {
  let buffered = Buffer.alloc(0);
  let fragments: Buffer[] = [];
  return chunk => {
    buffered = Buffer.concat([buffered, chunk]);
    const output: Buffer[] = [];
    while (buffered.length >= 2) {
      const opcode = buffered[0]! & 15;
      const masked = (buffered[1]! & 128) !== 0;
      let size = buffered[1]! & 127;
      let at = 2;
      if (size === 126) {
        if (buffered.length < 4) break;
        size = buffered.readUInt16BE(2);
        at = 4;
      } else if (size === 127) {
        if (buffered.length < 10) break;
        size = Number(buffered.readBigUInt64BE(2));
        at = 10;
      }
      const start = at + (masked ? 4 : 0);
      if (buffered.length < start + size) break;
      const wire = buffered.subarray(0, start + size);
      const body = Buffer.from(wire.subarray(start));
      if (masked) for (let i = 0; i < size; i++) body[i] = body[i]! ^ wire[at + i % 4]!;
      buffered = buffered.subarray(wire.length);
      let forward = true;
      if (opcode === 1 || opcode === 0) {
        fragments.push(body);
        if ((wire[0]! & 128) !== 0) {
          forward = consume(JSON.parse(Buffer.concat(fragments).toString('utf8')) as Record<string, unknown>);
          fragments = [];
        }
      }
      if (forward) output.push(wire);
    }
    return output;
  };
}

export async function sessionProxy(mode: string) {
  const sockets = new Set<Duplex>();
  const requests: string[] = [];
  let upgrades = 0;
  let dropped = false;
  let target: unknown;
  const server = createServer((incoming, response) => {
    if (incoming.url === '/cut') {
      for (const socket of sockets) socket.destroy();
      dropped = true;
      response.end();
      return;
    }
    const outbound = request(new URL(incoming.url!, 'https://api.terrascale.tech'), {
      method: incoming.method, headers: { ...incoming.headers, host: 'api.terrascale.tech' },
    }, received => {
      response.writeHead(received.statusCode!, received.headers);
      received.pipe(response);
    });
    outbound.on('error', error => response.destroy(error));
    incoming.pipe(outbound);
  });
  server.on('upgrade', (incoming, client, head) => {
    upgrades++;
    sockets.add(client);
    const outbound = request(new URL(incoming.url!, 'https://api.terrascale.tech'), {
      headers: { ...incoming.headers, host: 'api.terrascale.tech' },
    });
    outbound.on('upgrade', (response, upstream, upstreamHead) => {
      sockets.add(upstream);
      client.write(`HTTP/1.1 101 Switching Protocols\r\n${Object.entries(response.headers).map(([k, v]) => `${k}: ${v}`).join('\r\n')}\r\n\r\n`);
      const fromClient = frames(body => {
        if (body.type !== 'auth') requests.push(String(body.type));
        if (body.type === 'commit' || ['autocommit', 'batch'].includes(mode) && body.type === 'execute' || mode === 'read' && body.type === 'execute' && requests.filter(type => type === 'execute').length === 2) target = body.id;
        return true;
      });
      const fromServer = frames(body => {
        if (mode !== 'before' && target !== undefined && body.id === target) {
          if (body.type !== 'result') throw new Error('the server did not commit successfully');
          dropped = true;
          client.destroy();
          upstream.destroy();
          return false;
        }
        return true;
      });
      client.on('data', chunk => { for (const frame of fromClient(Buffer.from(chunk))) upstream.write(frame); });
      upstream.on('data', chunk => { for (const frame of fromServer(Buffer.from(chunk))) client.write(frame); });
      client.on('close', () => { sockets.delete(client); upstream.destroy(); });
      upstream.on('close', () => { sockets.delete(upstream); client.destroy(); });
      client.on('error', () => upstream.destroy());
      upstream.on('error', () => client.destroy());
      if (head.length) client.emit('data', head);
      if (upstreamHead.length) upstream.emit('data', upstreamHead);
    });
    outbound.on('error', error => client.destroy(error));
    outbound.end();
  });
  await new Promise<void>(resolve => server.listen(0, '127.0.0.1', resolve));
  const address = server.address();
  if (address === null || typeof address === 'string') throw new Error('proxy address');
  return {
    origin: `http://127.0.0.1:${address.port}`,
    proof: () => ({ requests, upgrades, dropped }),
    close: async () => {
      for (const socket of sockets) socket.destroy();
      server.closeAllConnections();
      await new Promise<void>((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
    },
  };
}
