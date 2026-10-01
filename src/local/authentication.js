import { createHash, timingSafeEqual } from "node:crypto";

const MINIMUM_BEARER_TOKEN_LENGTH = 32;
const MAXIMUM_BEARER_TOKEN_LENGTH = 512;
const BEARER_TOKEN_PATTERN = /^[A-Za-z0-9._~+\/-]+=*$/u;

/**
 * @param {unknown} value
 * @returns {string}
 */
export function normalizeLocalBearerToken(value) {
  if (typeof value !== "string") {
    throw new TypeError('bearerToken is required when authentication is "required".');
  }
  if (
    value.length < MINIMUM_BEARER_TOKEN_LENGTH
    || value.length > MAXIMUM_BEARER_TOKEN_LENGTH
    || !BEARER_TOKEN_PATTERN.test(value)
  ) {
    throw new TypeError(
      `bearerToken must contain between ${MINIMUM_BEARER_TOKEN_LENGTH} and ${MAXIMUM_BEARER_TOKEN_LENGTH} ASCII bearer-token characters.`,
    );
  }
  return value;
}

/**
 * @param {string | null} authorization
 * @param {string} expectedToken
 * @returns {boolean}
 */
export function matchesLocalBearerAuthorization(authorization, expectedToken) {
  if (authorization === null) {
    return false;
  }
  const match = /^Bearer ([A-Za-z0-9._~+\/-]+=*)$/iu.exec(authorization.trim());
  if (match === null) {
    return false;
  }
  const candidateToken = match[1];
  if (candidateToken === undefined) {
    return false;
  }
  const expectedDigest = createHash("sha256").update(expectedToken).digest();
  const candidateDigest = createHash("sha256").update(candidateToken).digest();
  return timingSafeEqual(expectedDigest, candidateDigest);
}
