import { createHash } from 'node:crypto';
import { createServer } from 'node:http';

const digest = createHash('sha1').update('TerraScale-journey-breached-2026!').digest('hex').toUpperCase();
const calls = [];
const script = `for (const widget of document.querySelectorAll('.cf-turnstile')) {
  const input = document.createElement('input');
  input.type = 'hidden';
  input.name = 'cf-turnstile-response';
  input.value = ['XXXX.DUMMY.TOKEN.XXXX', location.hostname, widget.dataset.action, widget.dataset.cdata].join('|');
  widget.closest('form').append(input);
}`;
createServer(async (request, response) => {
  const url = new URL(request.url, 'http://127.0.0.1');
  const send = (status, body, type = 'application/json') => {
    response.writeHead(status, { 'content-type': type });
    response.end(body);
  };
  if (url.pathname === '/calls') return send(200, JSON.stringify(calls));
  if (request.headers.host === 'api.pwnedpasswords.com' && /^\/range\/[A-F0-9]{5}$/.test(url.pathname)
      && request.headers['add-padding'] === 'true' && request.method === 'GET') {
    calls.push({ prefix: url.pathname.slice(7), padded: true });
    const match = url.pathname.slice(7) === digest.slice(0, 5) ? digest.slice(5) + ':42\r\n' : '';
    return send(200, match + '00000000000000000000000000000000000:0\r\n', 'text/plain');
  }
  if (request.headers.host === 'challenges.cloudflare.com' && url.pathname === '/turnstile/v0/api.js')
    return send(200, script, 'text/javascript');
  if (request.headers.host === 'challenges.cloudflare.com' && url.pathname === '/turnstile/v0/siteverify' && request.method === 'POST') {
    let body = '';
    for await (const chunk of request) body += chunk;
    const form = new URLSearchParams(body);
    if (form.get('response') === 'unreachable') return request.socket.destroy();
    const [token, hostname, action, cdata] = (form.get('response') ?? '').split('|');
    return send(200, JSON.stringify({ hostname, action, cdata, success: form.get('secret') === '1x0000000000000000000000000000000AA'
      && token === 'XXXX.DUMMY.TOKEN.XXXX' && !!form.get('remoteip') }));
  }
  return send(400, '{"error":"unexpected_attack_protection_request"}');
}).listen(8029, '127.0.0.1');
