import { createServer } from 'node:http'
import { randomBytes, createHash } from 'node:crypto'
import { readFile } from 'node:fs/promises'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { createRemoteJWKSet, jwtVerify } from 'jose'

const root = dirname(fileURLToPath(import.meta.url))

const loadEnv = async () => {
  try {
    const text = await readFile(join(root, '.env'), 'utf8')
    for (const line of text.split('\n')) {
      const match = line.match(/^([A-Z_]+)=(.*)$/)
      if (match && process.env[match[1]] === undefined) process.env[match[1]] = match[2]
    }
  } catch {}
}

await loadEnv()

const ISSUER = process.env.ISSUER
const CLIENT_ID = process.env.CLIENT_ID
const CLIENT_SECRET = process.env.CLIENT_SECRET
const REDIRECT_URI = process.env.REDIRECT_URI ?? 'http://localhost:3000/callback'
const PORT = Number(process.env.PORT ?? 3000)
const SCOPES = 'openid email profile offline_access'

if (!ISSUER || !CLIENT_ID) {
  console.error('Set ISSUER and CLIENT_ID (copy .env.example to .env and fill it in).')
  process.exit(1)
}

const discoveryUrl = `${ISSUER.replace(/\/$/, '')}/.well-known/openid-configuration`
const discovery = await fetch(discoveryUrl).then((r) => {
  if (!r.ok) throw new Error(`discovery failed: ${r.status}`)
  return r.json()
})

const authorizationEndpoint = discovery.authorization_endpoint
const tokenEndpoint = discovery.token_endpoint
const endSessionEndpoint = discovery.end_session_endpoint ?? `${ISSUER.replace(/\/$/, '')}/oauth/logout`
const jwks = createRemoteJWKSet(new URL(discovery.jwks_uri))

const sessions = new Map()
const attempts = new Map()

const randomToken = () => randomBytes(32).toString('base64url')
const sha256 = (value) => createHash('sha256').update(value).digest('base64url')

const parseCookies = (req) => {
  const header = req.headers.cookie
  const jar = {}
  if (!header) return jar
  for (const part of header.split(';')) {
    const eq = part.indexOf('=')
    if (eq > 0) jar[part.slice(0, eq).trim()] = decodeURIComponent(part.slice(eq + 1).trim())
  }
  return jar
}

const send = (res, status, body, headers = {}) => {
  res.writeHead(status, headers)
  res.end(body)
}

const sendHtml = (res, status, html) =>
  send(res, status, html, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' })

const redirect = (res, location) => send(res, 302, '', { location })

const esc = (value) =>
  String(value).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c])

const tokenRequest = async (params) => {
  const body = new URLSearchParams(params)
  const headers = { 'content-type': 'application/x-www-form-urlencoded' }
  if (CLIENT_SECRET) {
    headers.authorization = `Basic ${Buffer.from(`${CLIENT_ID}:${CLIENT_SECRET}`).toString('base64')}`
  } else {
    body.set('client_id', CLIENT_ID)
  }
  const response = await fetch(tokenEndpoint, { method: 'POST', headers, body })
  const json = await response.json()
  if (!response.ok) throw new Error(json.error ?? `token endpoint returned ${response.status}`)
  return json
}

const verifyIdToken = async (idToken, nonce) => {
  const { payload } = await jwtVerify(idToken, jwks, {
    issuer: ISSUER.replace(/\/$/, ''),
    audience: CLIENT_ID,
    requiredClaims: ['exp', 'iat', 'nonce'],
  })
  if (payload.nonce !== nonce) throw new Error('nonce mismatch')
  return payload
}

const homePage = (session) => {
  if (!session) {
    return `<!doctype html><html><body style="font-family:system-ui;margin:4rem auto;max-width:32rem">
<h1>Sign in with TerraScale</h1>
<p><a href="/login">Sign in</a></p>
</body></html>`
  }
  const claims = Object.entries(session.claims)
    .map(([key, value]) => `<tr><td><code>${esc(key)}</code></td><td><code>${esc(JSON.stringify(value))}</code></td></tr>`)
    .join('\n')
  return `<!doctype html><html><body style="font-family:system-ui;margin:4rem auto;max-width:40rem">
<h1>Signed in</h1>
<table border="1" cellpadding="6" style="border-collapse:collapse">${claims}</table>
<p><a href="/refresh">Refresh tokens</a> · <a href="/logout">Sign out</a></p>
</body></html>`
}

const server = createServer(async (req, res) => {
  const url = new URL(req.url, `http://${req.headers.host}`)
  const cookies = parseCookies(req)
  try {
    if (url.pathname === '/' && req.method === 'GET') {
      return sendHtml(res, 200, homePage(sessions.get(cookies.sid)))
    }

    if (url.pathname === '/login' && req.method === 'GET') {
      const sid = randomToken()
      const attempt = { state: randomToken(), nonce: randomToken(), verifier: randomToken() }
      attempts.set(attempt.state, { sid, ...attempt, used: false })
      const authorizeUrl = new URL(authorizationEndpoint)
      authorizeUrl.searchParams.set('response_type', 'code')
      authorizeUrl.searchParams.set('client_id', CLIENT_ID)
      authorizeUrl.searchParams.set('redirect_uri', REDIRECT_URI)
      authorizeUrl.searchParams.set('scope', SCOPES)
      authorizeUrl.searchParams.set('state', attempt.state)
      authorizeUrl.searchParams.set('nonce', attempt.nonce)
      authorizeUrl.searchParams.set('code_challenge', sha256(attempt.verifier))
      authorizeUrl.searchParams.set('code_challenge_method', 'S256')
      res.setHeader('set-cookie', `sid=${sid}; HttpOnly; SameSite=Lax; Path=/; Max-Age=3600`)
      return redirect(res, authorizeUrl)
    }

    if (url.pathname === '/callback' && req.method === 'GET') {
      const sid = cookies.sid
      const state = url.searchParams.get('state')
      const attempt = attempts.get(state)
      if (!sid || !attempt || attempt.used || attempt.sid !== sid) {
        return sendHtml(res, 400, '<!doctype html><html><body><h1>Invalid sign-in attempt</h1></body></html>')
      }
      attempt.used = true
      attempts.delete(state)
      const error = url.searchParams.get('error')
      if (error) {
        return sendHtml(res, 400, `<!doctype html><html><body><h1>Sign-in failed</h1><p>${esc(error)}</p></body></html>`)
      }
      const code = url.searchParams.get('code')
      if (!code) return sendHtml(res, 400, '<!doctype html><html><body><h1>Missing code</h1></body></html>')
      const tokens = await tokenRequest({
        grant_type: 'authorization_code',
        code,
        redirect_uri: REDIRECT_URI,
        code_verifier: attempt.verifier,
      })
      const claims = await verifyIdToken(tokens.id_token, attempt.nonce)
      sessions.set(sid, {
        claims,
        idToken: tokens.id_token,
        refreshToken: tokens.refresh_token,
      })
      return redirect(res, '/')
    }

    if (url.pathname === '/refresh' && req.method === 'GET') {
      const sid = cookies.sid
      const session = sessions.get(sid)
      if (!session?.refreshToken) return redirect(res, '/login')
      const tokens = await tokenRequest({
        grant_type: 'refresh_token',
        refresh_token: session.refreshToken,
      })
      session.refreshToken = tokens.refresh_token ?? session.refreshToken
      if (tokens.id_token) {
        const claims = await jwtVerify(tokens.id_token, jwks, {
          issuer: ISSUER.replace(/\/$/, ''),
          audience: CLIENT_ID,
          requiredClaims: ['exp', 'iat'],
        })
        session.claims = claims.payload
      }
      return redirect(res, '/')
    }

    if (url.pathname === '/logout' && req.method === 'GET') {
      const sid = cookies.sid
      const session = sessions.get(sid)
      if (sid) sessions.delete(sid)
      if (session?.refreshToken) {
        try {
          await fetch(discovery.revocation_endpoint ?? `${ISSUER.replace(/\/$/, '')}/oauth/revoke`, {
            method: 'POST',
            headers: { 'content-type': 'application/x-www-form-urlencoded' },
            body: new URLSearchParams({ token: session.refreshToken }),
          })
        } catch {}
      }
      res.setHeader('set-cookie', 'sid=; HttpOnly; SameSite=Lax; Path=/; Max-Age=0')
      if (endSessionEndpoint) {
        const logoutUrl = new URL(endSessionEndpoint)
        logoutUrl.searchParams.set('post_logout_redirect_uri', `${REDIRECT_URI.replace(/\/callback$/, '')}/`)
        if (session?.idToken) logoutUrl.searchParams.set('id_token_hint', session.idToken)
        return redirect(res, logoutUrl)
      }
      return redirect(res, '/')
    }

    return sendHtml(res, 404, '<!doctype html><html><body><h1>Not found</h1></body></html>')
  } catch (err) {
    console.error(err.message)
    return sendHtml(res, 500, '<!doctype html><html><body><h1>Something went wrong</h1></body></html>')
  }
})

server.listen(PORT, () => {
  console.log(`oidc-customer-app listening on http://localhost:${PORT}`)
  console.log(`issuer: ${ISSUER}`)
  console.log(`redirect URI: ${REDIRECT_URI}`)
})
