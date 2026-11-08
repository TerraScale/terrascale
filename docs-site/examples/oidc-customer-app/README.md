# Sign in with TerraScale sample app

A minimal Node.js app that signs end users in to a TerraScale project with the authorization code flow and PKCE (`S256`), validates ID tokens, refreshes tokens and signs out. It uses `node:http` with a single runtime dependency ([`jose`](https://www.npmjs.com/package/jose)) for JWKS fetching and ID token verification.

All OAuth endpoints (authorization, token, JWKS, revocation, end-session) are read from your project's discovery document at `<issuer>/.well-known/openid-configuration` at startup; nothing is hardcoded.

## Run it

Requires Node.js 22 or newer.

1. In your project dashboard, under **Authentication** > **OAuth applications**, register a **confidential** application (or **public** if you skip the secret) with redirect URI `http://localhost:3000/callback`.

2. Copy `.env.example` to `.env` and fill in your project issuer (`https://auth.terrascale.tech/<project ID>`) and the application's client ID (and client secret for confidential clients).

3. Start the app:

   ```sh
   npm install
   npm start
   ```

Open `http://localhost:3000` and click **Sign in**.

## What it does

- `/` shows the validated ID token claims when signed in, otherwise a sign-in link.
- `/login` creates a single-use sign-in attempt with cryptographically random `state`, `nonce` and PKCE verifier, stored server-side in an in-memory session keyed by an `HttpOnly`, `SameSite=Lax` cookie, then redirects to the discovered authorization endpoint with `code_challenge_method=S256`.
- `/callback` checks `state` against the initiating session, exchanges the code with the `code_verifier`, and verifies the ID token with `jose.jwtVerify` against the issuer's JWKS (`iss`, `aud`, `exp`, `iat`, `nonce`).
- `/refresh` uses the refresh token grant; refresh tokens rotate on every use, so the app keeps the successor.
- `/logout` clears the session, revokes the refresh token at the discovered `revocation_endpoint`, and redirects to the end-session endpoint.

Sessions live in memory for the process lifetime only; a real application persists them in its own store. Tokens and secrets are never logged or placed in URLs.
