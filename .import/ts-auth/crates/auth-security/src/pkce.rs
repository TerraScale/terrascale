//! OAuth 2.0 PKCE S256 (RFC 7636).

use base64::{Engine, engine::general_purpose::URL_SAFE_NO_PAD};
use sha2::{Digest, Sha256};
use subtle::ConstantTimeEq;

/// The verifier grammar from RFC 7636 section 4.1: 43–128 unreserved ASCII
/// characters. Restricting the alphabet also keeps ASCII and UTF-8 hashing
/// equivalent, as required by S256.
pub fn valid_verifier(verifier: &str) -> bool {
    (43..=128).contains(&verifier.len())
        && verifier
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'.' | b'_' | b'~'))
}

pub fn challenge_s256(verifier: &str) -> Option<String> {
    valid_verifier(verifier).then(|| URL_SAFE_NO_PAD.encode(Sha256::digest(verifier.as_bytes())))
}

pub fn verify_s256(verifier: &str, challenge: &str) -> bool {
    if challenge.len() != 43 {
        return false;
    }
    challenge_s256(verifier)
        .is_some_and(|derived| bool::from(derived.as_bytes().ct_eq(challenge.as_bytes())))
}
