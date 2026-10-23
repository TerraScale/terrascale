// The TerraBase shape edge: free reads tier T2 (docs/terrabase/free-reads-edge.md). Serves mirrored TBS1 shape objects
// from Cloudflare's cache. The manifest links each object as https://<edge>/<bucket host>/<bucket>/<key>?<SigV4 query>.
// The cache key drops the query, so every reader at one Cloudflare location shares one copy no matter when its URL
// was signed. That is safe because objects are immutable, keys are SHA-256 names and the bytes are ciphertext without
// the epoch key. A miss forwards the signed request unchanged; the bucket checks the signature.

// S3-compatible bucket hosts the edge may read: Tigris and R2. Anything else is refused, so this is no open proxy.
const BUCKET_HOSTS = [/^t3\.storage\.dev$/, /^fly\.storage\.tigris\.dev$/, /^[0-9a-f]{32}\.r2\.cloudflarestorage\.com$/];
const OBJECT = /^\/([a-z0-9.-]+)\/([a-z0-9][a-z0-9.-]{1,61}[a-z0-9])\/v1\/shapes\/h[0-9]-[0-9a-f]{16}\/[0-9a-f]{64}$/;

export default {
  async fetch(request, env, ctx) {
    if (request.method !== 'GET' && request.method !== 'HEAD') {
      return new Response('method not allowed', { status: 405, headers: { allow: 'GET, HEAD' } });
    }

    const url = new URL(request.url);
    const match = OBJECT.exec(url.pathname);
    if (!match || !BUCKET_HOSTS.some((host) => host.test(match[1]))) {
      return new Response('not found', { status: 404 });
    }

    // An expired or unsigned URL is refused even on a hit: a link keeps the bucket's expiry.
    if (!current(url.searchParams, Date.now())) {
      return new Response('the URL is not signed or has expired', { status: 403 });
    }

    const key = new Request(`https://${url.host}${url.pathname}`);
    const cached = await caches.default.match(key);
    if (cached) {
      return answer(cached, request, 'HIT');
    }

    const origin = await fetch(`https://${url.pathname.slice(1)}${url.search}`, { method: 'GET' });
    if (origin.status === 200 && /immutable/.test(origin.headers.get('cache-control') ?? '')) {
      ctx.waitUntil(caches.default.put(key, origin.clone()));
    }

    return answer(origin, request, 'MISS');
  },
};

// SigV4 query: X-Amz-Date (YYYYMMDDTHHMMSSZ) plus X-Amz-Expires seconds.
export function current(query, now) {
  const date = /^(\d{4})(\d{2})(\d{2})T(\d{2})(\d{2})(\d{2})Z$/.exec(query.get('X-Amz-Date') ?? '');
  const expires = Number(query.get('X-Amz-Expires'));
  if (!date || !Number.isInteger(expires) || expires <= 0 || !query.get('X-Amz-Signature')) {
    return false;
  }

  const signed = Date.UTC(+date[1], +date[2] - 1, +date[3], +date[4], +date[5], +date[6]);
  return now < signed + expires * 1000;
}

function answer(response, request, status) {
  const headers = new Headers(response.headers);
  headers.set('tb-edge-cache', status);
  return new Response(request.method === 'HEAD' ? null : response.body, { status: response.status, headers });
}
