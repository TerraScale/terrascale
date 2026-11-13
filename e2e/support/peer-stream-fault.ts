import { EventEmitter, once } from 'node:events';
import { connect, createServer, constants, type ServerHttp2Session, type ServerHttp2Stream, type IncomingHttpHeaders } from 'node:http2';

export async function holdPeerStream(address: string, port: number, destination: string, path: string) {
  const server = createServer();
  const sessions = new Set<ServerHttp2Session>();
  const events = new EventEmitter();
  server.on('session', session => {
    sessions.add(session);
    session.on('error', error => events.emit('transportError', error));
    session.once('close', () => sessions.delete(session));
  });
  server.on('stream', (stream: ServerHttp2Stream, headers: IncomingHttpHeaders) => {
    if (headers[':path'] === path) {
      events.emit('blocked');
      return;
    }
    const peer = connect(destination);
    const outgoing = peer.request({ ...headers, ':authority': new URL(destination).host });
    peer.on('error', () => stream.close(constants.NGHTTP2_CANCEL));
    outgoing.on('error', () => stream.close(constants.NGHTTP2_CANCEL));
    outgoing.once('response', response => { if (!stream.closed && !stream.destroyed) stream.respond(response); });
    outgoing.once('close', () => peer.destroy());
    stream.once('close', () => peer.destroy());
    stream.pipe(outgoing);
    outgoing.pipe(stream);
  });
  const listening = once(server, 'listening');
  server.listen(port, address);
  await listening;
  return {
    events,
    ping: async () => {
      const probe = connect('http://' + address + ':' + port);
      try {
        await once(probe, 'connect');
        await new Promise<void>((resolve, reject) => probe.ping(error => error ? reject(error) : resolve()));
      } finally {
        probe.destroy();
      }
    },
    close: async () => {
      for (const session of sessions) session.destroy();
      await new Promise<void>((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
    },
  };
}

export async function holdPeerReplies(address: string, port: number, destination: string, path: string) {
  const server = createServer({ settings: { enableConnectProtocol: true } });
  const sessions = new Set<ServerHttp2Session>();
  const events = new EventEmitter();
  const held: (() => void)[] = [];
  let holding = false;
  server.on('session', session => {
    sessions.add(session);
    session.on('error', error => events.emit('transportError', error));
    session.once('close', () => sessions.delete(session));
  });
  server.on('stream', async (stream: ServerHttp2Stream, headers: IncomingHttpHeaders) => {
    const peer = connect(destination);
    if (headers[':protocol']) await once(peer, 'remoteSettings');
    const outgoing = peer.request({ ...headers, ':authority': new URL(destination).host });
    peer.on('error', () => stream.close(constants.NGHTTP2_CANCEL));
    outgoing.on('error', () => stream.close(constants.NGHTTP2_CANCEL));
    outgoing.once('response', response => { if (!stream.closed && !stream.destroyed) stream.respond(response); });
    outgoing.once('close', () => peer.destroy());
    stream.once('close', () => peer.destroy());
    stream.pipe(outgoing);
    if (headers[':path'] !== path) {
      outgoing.pipe(stream);
      return;
    }
    events.emit('opened');
    const send = (deliver: () => void) => {
      if (!holding && held.length === 0) {
        deliver();
        return;
      }
      held.push(deliver);
      events.emit('held');
    };
    outgoing.on('data', (chunk: Buffer) => send(() => { if (!stream.destroyed) stream.write(chunk); }));
    outgoing.once('end', () => send(() => { if (!stream.destroyed) stream.end(); }));
  });
  const listening = once(server, 'listening');
  server.listen(port, address);
  await listening;
  return {
    events,
    cut: () => { for (const session of sessions) session.destroy(); held.splice(0); },
    hold: () => { holding = true; },
    release: () => {
      holding = false;
      for (const deliver of held.splice(0)) deliver();
    },
    close: async () => {
      for (const session of sessions) session.destroy();
      await new Promise<void>((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
    },
  };
}
