import { randomBytes } from 'node:crypto';
import { connect } from 'node:net';

export async function natsRequest(token, subject, value = {}, port = 4222) {
  return new Promise((resolve, reject) => {
    const socket = connect({ host: '127.0.0.1', port });
    const inbox = '_INBOX.' + randomBytes(12).toString('hex');
    const payload = Buffer.from(JSON.stringify(value));
    let buffered = Buffer.alloc(0);
    let length;
    socket.setTimeout(15_000, () => socket.destroy(new Error('broker request timed out')));
    socket.once('error', reject);
    socket.on('connect', () => socket.write('CONNECT ' + JSON.stringify({ auth_token: token, verbose: false }) + '\r\nSUB ' + inbox + ' 1\r\nPUB ' + subject + ' ' + inbox + ' ' + payload.length + '\r\n' + payload + '\r\n'));
    socket.on('data', chunk => {
      buffered = Buffer.concat([buffered, chunk]);
      while (true) {
        if (length !== undefined) {
          if (buffered.length < length + 2) return;
          const answer = buffered.subarray(0, length);
          socket.end();
          try {
            const result = JSON.parse(answer.toString());
            if (result.error) reject(new Error('broker API: ' + result.error.description));
            else resolve(result);
          } catch (error) { reject(error); }
          return;
        }
        const end = buffered.indexOf('\r\n');
        if (end < 0) return;
        const line = buffered.subarray(0, end).toString();
        buffered = buffered.subarray(end + 2);
        if (line === 'PING') socket.write('PONG\r\n');
        if (line.startsWith('-ERR')) socket.destroy(new Error('broker rejected proof request'));
        if (line.startsWith('MSG ')) length = Number(line.split(' ').at(-1));
      }
    });
  });
}
