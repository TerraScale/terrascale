import { createHash, generateKeyPairSync, randomBytes, sign } from 'node:crypto';
import { createServer } from 'node:http';

const issuer = 'https://login.journey-idp.example';
const { privateKey, publicKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
const jwk = { ...publicKey.export({ format: 'jwk' }), kid: 'journey', alg: 'RS256', use: 'sig' };
const domains = new Map();
const codes = new Map();
const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
createServer(async (request, response) => {
  const url = new URL(request.url, issuer);
  const send = (status, value) => {
    response.writeHead(status, { 'content-type': 'application/json' });
    response.end(JSON.stringify(value));
  };
  let body = '';
  for await (const chunk of request) body += chunk;
  if (url.pathname === '/configure' && request.method === 'POST') {
    const config = JSON.parse(body);
    domains.set(config.domain, config);
    return send(200, {});
  }
  if (url.pathname === '/.well-known/openid-configuration') return send(200, {
    issuer, authorization_endpoint: issuer + '/authorize', token_endpoint: issuer + '/token', jwks_uri: issuer + '/jwks',
    response_types_supported: ['code'], subject_types_supported: ['public'], id_token_signing_alg_values_supported: ['RS256'],
  });
  if (url.pathname === '/jwks') return send(200, { keys: [jwk] });
  if (url.pathname === '/authorize') {
    const email = url.searchParams.get('login_hint');
    const config = domains.get(email?.split('@')[1]);
    if (!config || url.searchParams.get('code_challenge_method') !== 'S256') return send(400, {});
    const code = randomBytes(24).toString('base64url');
    codes.set(code, { config: { ...config }, email, params: url.searchParams });
    const callback = new URL(url.searchParams.get('redirect_uri'));
    callback.search = new URLSearchParams({ code, state: url.searchParams.get('state') }).toString();
    response.writeHead(302, { location: callback.href });
    return response.end();
  }
  if (url.pathname === '/token' && request.method === 'POST') {
    const form = new URLSearchParams(body);
    const pending = codes.get(form.get('code'));
    codes.delete(form.get('code'));
    if (!pending || form.get('grant_type') !== 'authorization_code'
      || form.get('redirect_uri') !== pending.params.get('redirect_uri')
      || request.headers.authorization !== 'Basic ' + Buffer.from(pending.params.get('client_id') + ':journey-secret').toString('base64')
      || createHash('sha256').update(form.get('code_verifier') ?? '').digest('base64url') !== pending.params.get('code_challenge'))
      return send(400, { error: 'invalid_grant' });
    const claims = { iss: issuer, aud: pending.params.get('client_id'), sub: pending.config.subjects?.[pending.email] ?? pending.email, email: pending.email,
      nonce: pending.params.get('nonce'), exp: Math.floor(Date.now() / 1000) + 120 };
    if (pending.config.verified !== 'missing') claims.email_verified = pending.config.verified;
    const payload = encode({ alg: 'RS256', kid: 'journey', typ: 'JWT' }) + '.' + encode(claims);
    return send(200, { id_token: payload + '.' + sign('RSA-SHA256', Buffer.from(payload), privateKey).toString('base64url') });
  }
  return send(404, {});
}).listen(8033, '127.0.0.1');
