export const prerender = true

const publicRoutes = ['/', '/about', '/contact', '/pricing'] as const

export function GET() {
  const urls = publicRoutes
    .map((route) => `<url><loc>https://terrascale.tech${route}</loc></url>`)
    .join('')
  return new Response(
    `<?xml version="1.0" encoding="UTF-8"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">${urls}</urlset>`,
    { headers: { 'content-type': 'application/xml' } },
  )
}
