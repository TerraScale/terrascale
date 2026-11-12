import { createServer } from 'node:http';
import { createHmac, timingSafeEqual } from 'node:crypto';

let secret;
let failures = 0;
const deliveries = [];
const waiters = new Set();

function respond(response, status, body) {
  response.writeHead(status, { 'content-type': 'application/json' });
  response.end(JSON.stringify(body));
}

createServer(async (request, response) => {
  const url = new URL(request.url, 'http://127.0.0.1:8029');
  const chunks = [];
  for await (const chunk of request) chunks.push(chunk);
  const body = Buffer.concat(chunks);
  if (url.pathname === '/configure' && request.method === 'POST') {
    const config = JSON.parse(body);
    secret = config.secret;
    failures = config.failures;
    deliveries.length = 0;
    respond(response, 200, {});
  } else if (url.pathname === '/deliveries') {
    respond(response, 200, deliveries);
  } else if (url.pathname === '/wait') {
    const after = Number(url.searchParams.get('after') ?? 0);
    const predicate = () => deliveries.length > after;
    if (predicate()) respond(response, 200, deliveries);
    else {
      const notify = () => { if (predicate()) { waiters.delete(notify); respond(response, 200, deliveries); } };
      waiters.add(notify);
      response.on('close', () => waiters.delete(notify));
    }
  } else if (url.pathname === '/redirect') {
    response.writeHead(302, { location: 'https://hooks.journey.terrascale.tech/forbidden' });
    response.end();
  } else if (url.pathname === '/forbidden') {
    deliveries.push({ redirectFollowed: true });
    respond(response, 500, {});
  } else if (url.pathname === '/receive' && secret) {
    const id = request.headers['webhook-id'];
    const timestamp = request.headers['webhook-timestamp'];
    const expected = createHmac('sha256', Buffer.from(secret.slice(6), 'base64')).update(`${id}.${timestamp}.`).update(body).digest();
    const valid = Math.abs(Date.now() / 1000 - Number(timestamp)) <= 300 && String(request.headers['webhook-signature']).split(' ').some(signature => {
      if (!signature.startsWith('v1,')) return false;
      const value = Buffer.from(signature.slice(3), 'base64');
      return value.length === expected.length && timingSafeEqual(value, expected);
    });
    const code = valid ? failures-- > 0 ? 500 : 200 : 401;
    deliveries.push({ id, valid, code, receivedAt: performance.now(), event: JSON.parse(body) });
    respond(response, code, { received: valid, body: 'x'.repeat(2048) });
    for (const notify of waiters) notify();
  } else respond(response, 404, {});
}).listen(8029, '127.0.0.1');
