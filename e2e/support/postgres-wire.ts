import { connect, type Socket } from 'node:net';
import { EventEmitter, once } from 'node:events';
import { journey } from './state';

async function* messages(socket: Socket) {
  let buffered = Buffer.alloc(0);
  for await (const chunk of socket) {
    buffered = Buffer.concat([buffered, chunk]);
    while (buffered.length >= 5 && buffered.length >= 1 + buffered.readInt32BE(1)) {
      const length = buffered.readInt32BE(1);
      yield { type: String.fromCharCode(buffered[0]), body: buffered.subarray(5, length + 1) };
      buffered = buffered.subarray(length + 1);
    }
  }
}

export function message(type: string, body: Buffer): Buffer {
  const prefix = Buffer.alloc(5);
  prefix.write(type);
  prefix.writeInt32BE(body.length + 4, 1);
  return Buffer.concat([prefix, body]);
}

export async function postgres(port = 6432, database = journey().databaseId) {
  const current = journey();
  const socket = connect(port, '127.0.0.1');
  await once(socket, 'connect');
  const parameters = Buffer.from(`user\0any\0database\0${current.projectId}/${database}\0\0`);
  const prefix = Buffer.alloc(8);
  prefix.writeInt32BE(parameters.length + 8);
  prefix.writeInt32BE(196608, 4);
  let admitted!: (value: boolean) => void;
  const connected = new Promise<boolean>(resolve => { admitted = resolve; });
  const errors: string[] = [];
  const results = new EventEmitter();
  const closed = (async () => {
    for await (const packet of messages(socket)) {
      if (packet.type === 'R' && packet.body.readInt32BE() === 3) {
        socket.write(message('p', Buffer.from(current.apiKey + '\0')));
      }
      if (packet.type === 'Z') { admitted(true); results.emit('ready'); }
      if (packet.type === 'D') results.emit('row', packet.body.subarray(6).toString());
      if (packet.type === 'E') {
        errors.push(...packet.body.toString().split('\0').filter(Boolean));
        admitted(false);
      }
    }
    admitted(false);
    return errors;
  })();
  socket.write(Buffer.concat([prefix, parameters]));
  return {
    socket, connected, closed, results,
    begin: async () => {
      const ready = once(results, 'ready');
      socket.write(message('Q', Buffer.from('BEGIN\0')));
      await ready;
    },
    query: async () => {
      const row = once(results, 'row');
      socket.write(message('Q', Buffer.from('SELECT 42\0')));
      return (await row)[0] as string;
    },
  };
}

