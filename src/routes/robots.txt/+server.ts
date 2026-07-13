export const prerender = true

export function GET() {
  return new Response(
    'User-agent: *\nAllow: /\n\nSitemap: https://terrascale.tech/sitemap.xml\n',
    { headers: { 'content-type': 'text/plain; charset=utf-8' } },
  )
}
