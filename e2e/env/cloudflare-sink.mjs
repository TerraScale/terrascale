import { createServer } from 'node:http';

const messages = [];
const send = /^\/client\/v4\/accounts\/[^/]+\/email\/sending\/send$/;

function reply(response, status, body) {
  const text = JSON.stringify(body);
  response.writeHead(status, { 'content-type': 'application/json', 'content-length': Buffer.byteLength(text) });
  response.end(text);
}

createServer((request, response) => {
  const url = new URL(request.url, 'http://sink');
  if (request.method === 'POST' && send.test(url.pathname)) {
    const chunks = [];
    request.on('data', (chunk) => chunks.push(chunk));
    request.on('end', () => {
      messages.push({ at: new Date().toISOString(), bytes: Buffer.concat(chunks).length });
      reply(response, 200, { success: true, errors: [], messages: [], result: { delivered: [], queued: [], permanent_bounces: [], suppressed_recipients: [] } });
    });
    return;
  }
  if (request.method === 'GET' && url.pathname === '/messages') return reply(response, 200, messages);
  reply(response, 404, { success: false });
}).listen(8026, '127.0.0.1');
