// Exchange the API key for a short-lived access token (client credentials grant).
export async function accessToken(apiKey = process.env.TERRASCALE_API_KEY) {
  const keyId = apiKey.slice(0, apiKey.indexOf('_'))
  const response = await fetch('https://auth.terrascale.tech/terrascale/oauth/token', {
    method: 'POST',
    headers: { authorization: 'Basic ' + Buffer.from(`${keyId}:${apiKey}`).toString('base64') },
    body: new URLSearchParams({ grant_type: 'client_credentials' }),
  })
  if (!response.ok) throw new Error(`token exchange failed: HTTP ${response.status}`)
  return (await response.json()).access_token
}
