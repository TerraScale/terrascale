import { randomBytes } from 'node:crypto';
import { EventEmitter, once } from 'node:events';
import { request as upgrade } from 'node:http';
import type { Socket } from 'node:net';
import { expect } from '@playwright/test';

export function frame(opcode: number, payload: Buffer, final = true): Buffer {
  const mask = randomBytes(4);
  const masked = Buffer.from(payload);
  for (let i = 0; i < masked.length; i++) masked[i] ^= mask[i % 4];
  const prefix = Buffer.alloc(payload.length < 126 ? 2 : payload.length < 65536 ? 4 : 10);
  prefix[0] = (final ? 128 : 0) | opcode;
  prefix[1] = 128 | (prefix.length === 2 ? payload.length : prefix.length === 4 ? 126 : 127);
  if (prefix.length === 4) prefix.writeUInt16BE(payload.length, 2);
  if (prefix.length === 10) prefix.writeBigUInt64BE(BigInt(payload.length), 2);
  return Buffer.concat([prefix, mask, masked]);
}

export async function rawSqlSocket(url: string, credential?: string) {
  const request = upgrade(url, {
    headers: {
      connection: 'Upgrade', upgrade: 'websocket', 'sec-websocket-version': '13',
      'sec-websocket-key': randomBytes(16).toString('base64'), 'sec-websocket-protocol': 'terrascale.sql.v1',
      ...(credential ? { authorization: 'Bearer ' + credential } : {}),
    },
  });
  request.on('response', response => request.destroy(new Error('upgrade refused with ' + response.statusCode)));
  const upgraded = once(request, 'upgrade');
  request.end();
  const [response, socket, head] = await upgraded;
  expect(response.statusCode).toBe(101);
  const events = new EventEmitter();
  const firstMessage = once(events, 'message');
  let closeFrame!: (event: { code: number; reason: string }) => void;
  const closing = new Promise<{ code: number; reason: string }>(resolve => { closeFrame = resolve; });
  const closed = new Promise<void>(resolve => socket.once('close', () => {
    closeFrame({ code: 1006, reason: '' });
    resolve();
  }));
  socket.on('error', (error: Error) => events.emit('transportError', error));
  let buffered = Buffer.alloc(0);
  socket.on('data', (chunk: Buffer) => {
    buffered = Buffer.concat([buffered, chunk]);
    while (buffered.length >= 2) {
      const short = buffered[1] & 127;
      const prefix = short === 126 ? 4 : short === 127 ? 10 : 2;
      if (buffered.length < prefix) return;
      const length = short === 126 ? buffered.readUInt16BE(2) : short === 127 ? Number(buffered.readBigUInt64BE(2)) : short;
      if (buffered.length < prefix + length) return;
      const opcode = buffered[0] & 15;
      const payload = buffered.subarray(prefix, prefix + length);
      buffered = buffered.subarray(prefix + length);
      if (opcode === 1) events.emit('message', JSON.parse(payload.toString()));
      if (opcode === 9) socket.write(frame(10, payload));
      if (opcode === 8) {
        const event = { code: payload.readUInt16BE(), reason: payload.subarray(2).toString() };
        closeFrame(event);
        events.emit('closing', event);
      }
    }
  });

  if (head.length > 0) socket.unshift(head);
  return { socket: socket as Socket, closing, closed, events, firstMessage };
}

export function pacedWrite(socket: Socket, bytes: Buffer, bytesPerSecond: number, chunkBytes: number): Promise<boolean> {
  return new Promise((resolve, reject) => {
    const started = performance.now();
    let offset = 0;
    let timer: NodeJS.Timeout;
    const cleanup = () => {
      clearTimeout(timer);
      socket.off('close', closed);
      socket.off('error', failed);
    };
    const closed = () => { cleanup(); resolve(false); };
    const failed = (error: Error) => { cleanup(); reject(error); };
    const write = () => {
      const next = Math.min(bytes.length, offset + chunkBytes);
      socket.write(bytes.subarray(offset, next));
      offset = next;
      if (offset === bytes.length) {
        cleanup();
        resolve(true);
      } else {
        timer = setTimeout(write, Math.max(0, offset * 1000 / bytesPerSecond - (performance.now() - started)));
      }
    };
    socket.once('close', closed);
    socket.once('error', failed);
    write();
  });
}

export function scheduleStatement(socket: Socket, bytes: Buffer, began: number, elapsed: number): () => void {
  const timer = setTimeout(() => socket.write(bytes), Math.max(0, began + elapsed - performance.now()));
  return () => clearTimeout(timer);
}
