import { randomBytes } from 'node:crypto';
import { EventEmitter, once } from 'node:events';
import { request as upgrade } from 'node:http';
import type { Socket } from 'node:net';
import { expect } from '@playwright/test';

export function frame(opcode: number, payload: Buffer): Buffer {
  expect(payload.length).toBeLessThan(126);
  const mask = randomBytes(4);
  const masked = Buffer.from(payload);
  for (let i = 0; i < masked.length; i++) masked[i] ^= mask[i % 4];
  return Buffer.concat([Buffer.from([128 | opcode, 128 | payload.length]), mask, masked]);
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
  const closing = once(events, 'closing').then(([event]) => event as { code: number; reason: string });
  const closed = new Promise<void>(resolve => socket.once('close', resolve));
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
      if (opcode === 8) {
        events.emit('closing', { code: payload.readUInt16BE(), reason: payload.subarray(2).toString() });
      }
    }
  });

  if (head.length > 0) socket.unshift(head);
  return { socket: socket as Socket, closing, closed, events };
}
