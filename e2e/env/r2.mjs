import { createHash, createHmac, timingSafeEqual } from 'node:crypto';
import { existsSync, readFileSync } from 'node:fs';
import { createServer } from 'node:http';

const platformBuckets = new Set(['journey-backups', 'ts-terrabase-shapes-prod']);
const platformObject = /^\/(journey-backups|ts-terrabase-shapes-prod)\/((?:v1|catalog-anchor)\/[^?]+)$/;
const origin = 'https://dash.terrascale.tech';
const bucket = /^\/(terrascale-[a-z]+-files)\/(app-files\/[0-9A-Z]{26}\/[0-9A-Z]{26}\/[0-9A-Z]{26}\/[0-9A-Z]{26})$/;
const listing = /^\/(terrascale-[a-z]+-files)\/?$/;
const projectPrefix = /^app-files\/[0-9A-Z]{26}\/[0-9A-Z]{26}\/$/;
const listQuery = new Set(['list-type', 'prefix', 'max-keys', 'encoding-type']);
const objects = new Map();
const log = { calls: [], unexpected: [] };

const sha256 = (data, encoding) => createHash('sha256').update(data).digest(encoding);
const hmac = (key, data) => createHmac('sha256', key).update(data).digest();
const encode = (value) => encodeURIComponent(value).replace(/[!'()*]/g, (character) => `%${character.charCodeAt(0).toString(16).toUpperCase()}`);
const decode = (value) => decodeURIComponent(value.replace(/\+/g, '%20'));
const credentials = (requested) => {
  const [id, secret] = readFileSync(`${process.env.E2E_STATE}/r2`, 'utf8').split('\n');
  if (requested === id) return { id, secret };
  const path = `${process.env.E2E_STATE}/r2-accounts/${sha256(requested, 'hex')}`;
  if (!existsSync(path)) return null;
  const [account, key] = readFileSync(path, 'utf8').split('\n');
  return requested === account ? { id: account, secret: key } : null;
};

function amzTime(value) {
  const match = /^(\d{4})(\d{2})(\d{2})T(\d{2})(\d{2})(\d{2})Z$/.exec(value ?? '');
  return match ? Date.UTC(+match[1], +match[2] - 1, +match[3], +match[4], +match[5], +match[6]) : NaN;
}

function signed(request, path, query) {
  const presigned = query.has('X-Amz-Signature');
  const header = /^AWS4-HMAC-SHA256 Credential=([^,]+), ?SignedHeaders=([^,]+), ?Signature=([0-9a-f]{64})$/.exec(request.headers.authorization ?? '');
  const credential = presigned ? query.get('X-Amz-Credential') : header?.[1];
  const signedHeaders = presigned ? query.get('X-Amz-SignedHeaders') : header?.[2];
  const signature = presigned ? query.get('X-Amz-Signature') : header?.[3];
  const date = presigned ? query.get('X-Amz-Date') : request.headers['x-amz-date'];
  const payload = presigned ? 'UNSIGNED-PAYLOAD' : request.headers['x-amz-content-sha256'];
  if (!credential || !signedHeaders || !signature || !date || !payload) return 'unsigned request';
  if (presigned && query.get('X-Amz-Algorithm') !== 'AWS4-HMAC-SHA256') return 'unknown algorithm';
  const [id, day, region, service, terminal] = credential.split('/');
  const keys = credentials(id);
  if (!keys || id !== keys.id || region !== 'auto' || service !== 's3' || terminal !== 'aws4_request' || day !== date.slice(0, 8)) return 'unknown credential';
  const at = amzTime(date);
  const lifetime = presigned ? Number(query.get('X-Amz-Expires')) * 1000 : 15 * 60_000;
  if (!Number.isFinite(at) || !(lifetime > 0) || Date.now() > at + lifetime || at > Date.now() + 15 * 60_000) return 'expired signature';
  const names = signedHeaders.split(';');
  if (!names.includes('host')) return 'host is not signed';
  const canonicalHeaders = names.map((name) => `${name}:${String(request.headers[name] ?? '').trim().replace(/\s+/g, ' ')}\n`).join('');
  const canonicalQuery = [...query]
    .filter(([key]) => key !== 'X-Amz-Signature')
    .map(([key, value]) => [encode(key), encode(value)])
    .sort(([a, x], [b, y]) => (a < b ? -1 : a > b ? 1 : x < y ? -1 : x > y ? 1 : 0))
    .map(([key, value]) => `${key}=${value}`)
    .join('&');
  const canonical = [request.method, path, canonicalQuery, canonicalHeaders, signedHeaders, payload].join('\n');
  const scope = `${day}/${region}/${service}/${terminal}`;
  const key = hmac(hmac(hmac(hmac(`AWS4${keys.secret}`, day), region), service), terminal);
  const expected = createHmac('sha256', key).update(['AWS4-HMAC-SHA256', date, scope, sha256(canonical, 'hex')].join('\n')).digest();
  return timingSafeEqual(expected, Buffer.from(signature, 'hex')) ? { account: id } : 'signature mismatch';
}

function cors(response) {
  response.setHeader('access-control-allow-origin', origin);
  response.setHeader('vary', 'Origin');
}

function fail(response, status, code, detail) {
  const text = `<?xml version="1.0" encoding="UTF-8"?><Error><Code>${code}</Code><Message>${detail}</Message></Error>`;
  response.writeHead(status, { 'content-type': 'application/xml', 'content-length': Buffer.byteLength(text) });
  response.end(text);
}

function unexpected(request, response, status, code, detail) {
  log.unexpected.push(`${request.method} ${request.url.split('?')[0]}: ${detail}`);
  fail(response, status, code, detail);
}

function list(request, response, account, name, query) {
  const accountPrefix = `${account}/${name}/`;
  const prefix = query.get('prefix') ?? '';
  const limit = Number(query.get('max-keys') ?? 1000);
  const platform = platformBuckets.has(name);
  const encoded = query.has('encoding-type');
  if (query.get('list-type') !== '2' || !(platform ? prefix.startsWith('v1/') : projectPrefix.test(prefix)) || [...query.keys()].some((key) => !listQuery.has(key) && !(platform && key === 'start-after'))
    || !Number.isInteger(limit) || limit < 1 || limit > 1000 || (encoded && query.get('encoding-type') !== 'url'))
    return unexpected(request, response, 400, 'InvalidArgument', 'a listing that is not ListObjectsV2 of one project prefix');
  const text = (value) => (encoded ? value.split('/').map(encode).join('/') : value);
  const keys = [...objects.keys()].filter((key) => key.startsWith(`${accountPrefix}${prefix}`) && (!query.has('start-after') || key.slice(accountPrefix.length) > query.get('start-after'))).sort();
  const contents = keys.slice(0, limit).map((key) => {
    const stored = objects.get(key);
    return `<Contents><Key>${text(key.slice(accountPrefix.length))}</Key><LastModified>${new Date(stored.modified).toISOString()}</LastModified>`
      + `<ETag>${stored.etag.replaceAll('"', '&quot;')}</ETag><Size>${stored.body.length}</Size><StorageClass>STANDARD</StorageClass></Contents>`;
  });
  const more = keys.length > limit ? `<NextContinuationToken>${Buffer.from(keys[limit - 1].slice(account.length + 1)).toString('base64')}</NextContinuationToken>` : '';
  const body = `<?xml version="1.0" encoding="UTF-8"?><ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/"><Name>${name}</Name>`
    + `<Prefix>${text(prefix)}</Prefix><KeyCount>${contents.length}</KeyCount><MaxKeys>${limit}</MaxKeys>${encoded ? '<EncodingType>url</EncodingType>' : ''}`
    + `<IsTruncated>${keys.length > limit}</IsTruncated>${more}${contents.join('')}</ListBucketResult>`;
  response.writeHead(200, { 'content-type': 'application/xml', 'content-length': Buffer.byteLength(body) });
  response.end(body);
}

createServer((request, response) => {
  const [rawPath, rawQuery = ''] = request.url.split('?', 2);
  if (request.method === 'GET' && rawPath === '/log') {
    const text = JSON.stringify(log);
    response.writeHead(200, { 'content-type': 'application/json', 'content-length': Buffer.byteLength(text) });
    return response.end(text);
  }
  const query = new URLSearchParams();
  for (const pair of rawQuery.split('&').filter(Boolean)) {
    const at = pair.indexOf('=');
    query.append(decode(at < 0 ? pair : pair.slice(0, at)), at < 0 ? '' : decode(pair.slice(at + 1)));
  }
  const platform = platformObject.exec(rawPath);
  const target = bucket.exec(rawPath) ?? platform;
  const listed = request.method === 'GET' ? listing.exec(rawPath) : null;
  log.calls.push(`${request.method} ${target ? '/' + target[1] + '/<key>' : listed ? '/' + listed[1] + '?prefix=<project>' : rawPath}`);
  if (request.method === 'OPTIONS') {
    if (request.headers.origin !== origin || request.headers['access-control-request-method'] !== 'PUT' || !target)
      return unexpected(request, response, 403, 'CORSResponse', 'preflight from another origin, method or path');
    cors(response);
    response.writeHead(204, { 'access-control-allow-methods': 'PUT', 'access-control-allow-headers': 'content-type, x-amz-checksum-sha256', 'access-control-max-age': '600' });
    return response.end();
  }
  const platformBucket = /^\/(journey-backups|ts-terrabase-shapes-prod)\/?$/.exec(rawPath);
  if (!target && !listed && !platformBucket) return unexpected(request, response, 404, 'NoSuchBucket', 'path outside the files bucket');
  const chunks = [];
  request.on('data', (chunk) => chunks.push(chunk));
  request.on('end', () => {
    const body = Buffer.concat(chunks);
    if (request.headers.origin === origin) cors(response);
    const authenticated = signed(request, rawPath, query);
    if (typeof authenticated === 'string') return unexpected(request, response, 403, 'SignatureDoesNotMatch', authenticated);
    const { account } = authenticated;
    const key = target ? `${account}/${target[1]}/${target[2]}` : null;
    if (platform && query.has('uploadId')) return unexpected(request, response, 400, 'InvalidRequest', 'multipart uploads are not implemented');
    if (platform && /^[0-9a-f]{64}$/.test(request.headers['x-amz-content-sha256'] ?? '')
      && request.headers['x-amz-content-sha256'] !== sha256(body, 'hex'))
      return unexpected(request, response, 400, 'BadDigest', 'payload differs from its signed hash');
    if (platformBucket && request.method === 'HEAD') {
      response.writeHead(200, { 'content-length': 0 });
      return response.end();
    }
    if (platformBucket && (query.has('uploads') || query.has('versions'))) {
      const name = platformBucket[1];
      const accountPrefix = `${account}/${name}/`;
      const prefix = query.get('prefix') ?? '';
      const versions = query.has('versions');
      const root = versions ? 'ListVersionsResult' : 'ListMultipartUploadsResult';
      const entries = versions ? [...objects.keys()].filter((key) => key.startsWith(`${accountPrefix}${prefix}`)).map((key) => {
        const stored = objects.get(key);
        return `<Version><Key>${key.slice(accountPrefix.length)}</Key><VersionId>null</VersionId><IsLatest>true</IsLatest><LastModified>${new Date(stored.modified).toISOString()}</LastModified><ETag>${stored.etag.replaceAll('"', '&quot;')}</ETag><Size>${stored.body.length}</Size><StorageClass>STANDARD</StorageClass></Version>`;
      }).join('') : '';
      const text = `<?xml version="1.0" encoding="UTF-8"?><${root} xmlns="http://s3.amazonaws.com/doc/2006-03-01/"><Name>${name}</Name><Bucket>${name}</Bucket><Prefix>${prefix}</Prefix><IsTruncated>false</IsTruncated>${entries}</${root}>`;
      response.writeHead(200, { 'content-type': 'application/xml', 'content-length': Buffer.byteLength(text) });
      return response.end(text);
    }
    if (platformBucket && query.has('list-type')) return list(request, response, account, platformBucket[1], query);
    if (listed) return list(request, response, account, listed[1], query);
    if (request.method === 'PUT') {
      if (Number(request.headers['content-length']) !== body.length) return unexpected(request, response, 400, 'IncompleteBody', 'content-length differs from the body');
      if ((!platform || request.headers['x-amz-checksum-sha256']) && request.headers['x-amz-checksum-sha256'] !== sha256(body, 'base64')) return fail(response, 400, 'BadDigest', 'The SHA256 you specified did not match the calculated checksum.');
      objects.set(key, { body, type: request.headers['content-type'] ?? 'application/octet-stream', etag: `"${createHash('md5').update(body).digest('hex')}"`, modified: new Date().toUTCString() });
      response.writeHead(200, { etag: objects.get(key).etag, ...(request.headers['x-amz-checksum-sha256'] ? { 'x-amz-checksum-sha256': request.headers['x-amz-checksum-sha256'] } : {}), 'content-length': 0 });
      return response.end();
    }
    if (request.method === 'DELETE') {
      objects.delete(key);
      response.writeHead(204);
      return response.end();
    }
    if (request.method !== 'GET' && request.method !== 'HEAD') return unexpected(request, response, 405, 'MethodNotAllowed', 'unsupported method');
    const stored = objects.get(key);
    if (!stored) return fail(response, 404, 'NoSuchKey', 'The specified key does not exist.');
    response.writeHead(200, {
      'content-type': query.get('response-content-type') ?? stored.type,
      'content-length': stored.body.length,
      etag: stored.etag,
      'last-modified': stored.modified,
      ...(query.has('response-content-disposition') ? { 'content-disposition': query.get('response-content-disposition') } : {}),
      ...(query.has('response-cache-control') ? { 'cache-control': query.get('response-cache-control') } : {}),
    });
    response.end(request.method === 'HEAD' ? undefined : stored.body);
  });
}).listen(8028, '127.0.0.1');
