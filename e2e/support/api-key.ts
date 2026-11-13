export const apiKeyFormat = /^(?:sk_)?[0-9A-HJKMNP-TV-Z]{26}_[0-9a-hjkmnp-tv-z]{52}$/;

export function keyId(apiKey: string): string {
  const body = apiKey.startsWith('sk_') ? apiKey.slice(3) : apiKey;
  return body.slice(0, 26);
}
