import { createHash } from 'node:crypto';

// Per-page hashes keep CSP small across translated pages without allowing arbitrary inline scripts.
export function secureHtml(html) {
  const hashes = [...html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script(?=[\t\n\f\r />])[^>]*>/gi)]
    .filter(([, attributes, source]) => !/\bsrc\s*=/i.test(attributes) && source.trim())
    .map(([, , source]) => `'sha256-${createHash('sha256').update(source).digest('base64')}'`);
  const policy = [
    "default-src 'self'",
    `script-src 'self' https://static.cloudflareinsights.com ${[...new Set(hashes)].join(' ')}`,
    "style-src 'self' 'unsafe-inline'",
    "img-src 'self' data:",
    "font-src 'self'",
    "connect-src 'self' https://status.terrascale.tech https://cloudflareinsights.com",
    "worker-src 'self' blob:",
    "frame-src 'none'",
    "form-action 'none'",
    "base-uri 'self'",
    "object-src 'none'",
  ].join('; ');
  // The response header supplies frame-ancestors, which browsers ignore in a meta policy.
  if (!/<head\b[^>]*>/i.test(html)) throw new Error('Missing HTML head for CSP');
  return html.replace(/<head\b[^>]*>/i, `$&<meta http-equiv="Content-Security-Policy" content="${policy}">`);
}
