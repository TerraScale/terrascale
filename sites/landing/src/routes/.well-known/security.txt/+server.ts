export const prerender = true

export function GET() {
  return new Response(
    'Contact: mailto:security@terrascale.tech\nExpires: 2027-11-10T00:00:00.000Z\nPreferred-Languages: en\n',
    { headers: { 'content-type': 'text/plain; charset=utf-8' } },
  )
}
