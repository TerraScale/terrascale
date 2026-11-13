import { createServer } from 'node:http';
import { createHash, randomBytes } from 'node:crypto';

const codes = new Map();
const tokens = new Map();
const escape = value => value.replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');
const json = (response, status, body) => {
  response.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
  response.end(JSON.stringify(body));
};
const read = async request => {
  let body = '';
  for await (const chunk of request) body += chunk;
  return new URLSearchParams(body);
};
createServer(async (request, response) => {
  const url = new URL(request.url, 'https://identity.journey.terrascale.tech');
  if (request.method === 'GET' && url.pathname === '/readyz') return json(response, 200, {});
  if (request.method === 'GET' && url.pathname === '/authorize') {
    if (url.searchParams.get('client_id') !== 'journey-client' || url.searchParams.get('code_challenge_method') !== 'S256'
      || !url.searchParams.get('code_challenge') || !url.searchParams.get('state')
      || !/^https:\/\/auth\.terrascale\.tech\/ui\/[0-9A-HJKMNP-TV-Z]{26}\/social\/oauth2\/callback$/.test(url.searchParams.get('redirect_uri') ?? '')) {
      return json(response, 400, { error: 'invalid_request' });
    }
    response.writeHead(200, { 'Content-Type': 'text/html', 'Cache-Control': 'no-store' });
    response.end('<!doctype html><html lang="en"><title>OAuth2 identity</title><main><h1>OAuth2 identity</h1><form method="post" action="/authorize">'
      + [...url.searchParams].map(([key, value]) => '<input type="hidden" name="' + escape(key) + '" value="' + escape(value) + '">').join('')
      + '<label for="email">Email</label><input id="email" name="email" type="email" required>'
      + '<label for="subject">Subject</label><input id="subject" name="subject" required><label for="verified">Email verified</label><input id="verified" name="verified" type="checkbox" value="true"><button>Continue</button></form></main></html>');
    return;
  }
  if (request.method === 'POST' && url.pathname === '/authorize') {
    const form = await read(request);
    const code = randomBytes(24).toString('base64url');
    codes.set(code, { email: form.get('email'), subject: form.get('subject'), verified: form.get('verified') === 'true', redirect: form.get('redirect_uri'),
      challenge: form.get('code_challenge'), expires: Date.now() + 600_000 });
    const redirect = new URL(form.get('redirect_uri'));
    redirect.searchParams.set('code', code);
    redirect.searchParams.set('state', form.get('state'));
    response.writeHead(302, { Location: redirect.href, 'Cache-Control': 'no-store' });
    response.end();
    return;
  }
  if (request.method === 'POST' && url.pathname === '/token') {
    const form = await read(request);
    const issued = codes.get(form.get('code'));
    codes.delete(form.get('code'));
    const challenge = createHash('sha256').update(form.get('code_verifier') ?? '').digest('base64url');
    if (!issued || issued.expires <= Date.now() || issued.redirect !== form.get('redirect_uri') || issued.challenge !== challenge
      || form.get('client_id') !== 'journey-client' || !form.get('client_secret') || form.get('grant_type') !== 'authorization_code') {
      return json(response, 400, { error: 'invalid_grant' });
    }
    const token = randomBytes(24).toString('base64url');
    tokens.set(token, issued);
    return json(response, 200, { access_token: token, token_type: 'Bearer' });
  }
  if (request.method === 'GET' && url.pathname === '/userinfo') {
    const issued = tokens.get(request.headers.authorization?.replace(/^Bearer /, ''));
    if (!issued || issued.expires <= Date.now()) return json(response, 401, { error: 'invalid_token' });
    return json(response, 200, { id: issued.subject, email: issued.email, email_verified: issued.verified });
  }
  return json(response, 404, { error: 'not_found' });
}).listen(8031, '127.0.0.1');
