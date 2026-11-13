import { createCipheriv, createHash, createHmac, hkdfSync, randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { createConnection } from 'node:net';

export class Nats {
  buffer = Buffer.alloc(0);
  pending = new Map();
  next = 1;
  persistent = new Set();
  deadlines = new Set();
  constructor(token, port = 4222) {
    this.token = token;
    this.socket = createConnection({ port, host: '127.0.0.1' });
    this.socket.on('error', error => {
      for (const finish of this.pending.values()) finish({ error: { description: error.message } });
    });
    this.socket.on('data', chunk => {
      this.buffer = Buffer.concat([this.buffer, chunk]);
      while (true) {
        const end = this.buffer.indexOf('\r\n');
        if (end < 0) return;
        const line = this.buffer.subarray(0, end).toString();
        const parts = line.split(' ');
        if (parts[0] === 'MSG' || parts[0] === 'HMSG') {
          const size = Number(parts.at(-1));
          if (this.buffer.length < end + 2 + size + 2) return;
          const headerSize = parts[0] === 'HMSG' ? Number(parts.at(-2)) : 0;
          const data = this.buffer.subarray(end + 2 + headerSize, end + 2 + size);
          this.pending.get(parts[2])?.(data[0] === 123 ? JSON.parse(data) : { raw: data.toString(), subject: parts[1] });
          if (!this.persistent.has(parts[2])) this.pending.delete(parts[2]);
          this.buffer = this.buffer.subarray(end + 2 + size + 2);
        } else {
          if (line === 'PING') this.socket.write('PONG\r\n');
          if (line.startsWith('-ERR')) {
            for (const finish of this.pending.values()) finish({ error: { description: line } });
            this.pending.clear();
          }
          this.buffer = this.buffer.subarray(end + 2);
        }
      }
    });
  }
  async connect() {
    await once(this.socket, 'connect');
    this.socket.write('CONNECT ' + JSON.stringify({ auth_token: this.token, headers: true }) + '\r\n');
  }
  event(subject, matches = value => Boolean(value)) {
    const sid = String(this.next++);
    return new Promise((resolveEvent, reject) => {
      const deadline = setTimeout(() => reject(new Error('Missing NATS event: ' + subject)), 90_000);
      this.deadlines.add(deadline);
      this.persistent.add(sid);
      this.pending.set(sid, value => {
        if (value.error) { clearTimeout(deadline); this.deadlines.delete(deadline); reject(new Error(value.error.description)); return; }
        if (!matches(value)) return;
        clearTimeout(deadline);
        this.deadlines.delete(deadline);
        this.pending.delete(sid);
        this.persistent.delete(sid);
        this.socket.write('UNSUB ' + sid + '\r\n');
        resolveEvent(value);
      });
      this.socket.write('SUB ' + subject + ' ' + sid + '\r\n');
    });
  }
  close() {
    for (const deadline of this.deadlines) clearTimeout(deadline);
    this.socket.destroy();
  }
  async request(subject, payload = {}, headers) {
    const sid = String(this.next++);
    const inbox = '_INBOX.frontier.' + sid;
    const data = Buffer.isBuffer(payload) ? payload : Buffer.from(JSON.stringify(payload));
    const result = new Promise((resolveResult, reject) => {
      const deadline = setTimeout(() => reject(new Error('NATS request timed out: ' + subject)), 5000);
      this.deadlines.add(deadline);
      this.pending.set(sid, value => { clearTimeout(deadline); this.deadlines.delete(deadline); resolveResult(value); });
    });
    const prefix = 'SUB ' + inbox + ' ' + sid + '\r\nUNSUB ' + sid + ' 1\r\n';
    const block = headers ? Buffer.from('NATS/1.0\r\n' + Object.entries(headers).map(([key, value]) => key + ': ' + value + '\r\n').join('') + '\r\n') : Buffer.alloc(0);
    const publish = headers ? 'HPUB ' + subject + ' ' + inbox + ' ' + block.length + ' ' + (block.length + data.length) : 'PUB ' + subject + ' ' + inbox + ' ' + data.length;
    this.socket.write(Buffer.concat([Buffer.from(prefix + publish + '\r\n'), block, data, Buffer.from('\r\n')]));
    return result;
  }
}
const sha = value => createHash('sha256').update(value).digest('hex');
const hmac = (key, value) => createHmac('sha256', key).update(value).digest();
export async function putKey({ master, access, secret }, scope, keyId, dataKey) {
  const nonce = randomBytes(12);
  const cipher = createCipheriv('aes-256-gcm', master, nonce);
  const body = Buffer.from(Buffer.concat([nonce, cipher.update(dataKey), cipher.final(), cipher.getAuthTag()]).toString('base64'));
  const path = '/journey-shapes/v1/keys/' + scope + '/' + keyId;
  const host = 'journey.backups.terrascale.tech';
  const date = new Date().toISOString().replace(/[-:]|\.\d{3}/g, '');
  const names = 'host;x-amz-content-sha256;x-amz-date';
  const headers = 'host:' + host + '\nx-amz-content-sha256:' + sha(body) + '\nx-amz-date:' + date + '\n';
  const canonical = ['PUT', path, '', headers, names, sha(body)].join('\n');
  const credential = date.slice(0, 8) + '/auto/s3/aws4_request';
  const signing = hmac(hmac(hmac(hmac('AWS4' + secret, date.slice(0, 8)), 'auto'), 's3'), 'aws4_request');
  const signature = hmac(signing, ['AWS4-HMAC-SHA256', date, credential, sha(canonical)].join('\n')).toString('hex');
  const response = await fetch('https://' + host + path, { method: 'PUT', body, headers: {
    'x-amz-content-sha256': sha(body), 'x-amz-date': date,
    authorization: 'AWS4-HMAC-SHA256 Credential=' + access + '/' + credential + ', SignedHeaders=' + names + ', Signature=' + signature,
  } });
  if (response.status !== 200) throw new Error('data key upload: ' + response.status + ' ' + await response.text());
}
export function seal(transaction, scope, keyId, dataKey) {
  const json = Buffer.from(JSON.stringify(transaction));
  const lengths = [];
  for (let n = json.length - 15; n >= 0; n -= 255) {
    lengths.push(Math.min(n, 255));
    if (n < 255) break;
  }
  const size = Buffer.alloc(4);
  size.writeInt32BE(json.length);
  const packed = Buffer.concat([size, Buffer.from([Math.min(json.length, 15) << 4, ...lengths]), json]);
  const salt = randomBytes(32);
  const header = Buffer.concat([Buffer.from('TBP1'), Buffer.from(keyId), salt]);
  const key = hkdfSync('sha256', dataKey, salt, Buffer.from('TBP1 transaction\0' + scope), 32);
  const cipher = createCipheriv('aes-256-gcm', key, Buffer.alloc(12));
  cipher.setAAD(header);
  return Buffer.concat([header, cipher.update(packed), cipher.final(), cipher.getAuthTag()]);
}
