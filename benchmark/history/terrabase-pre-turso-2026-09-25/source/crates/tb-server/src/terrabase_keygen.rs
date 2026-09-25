//! Operator-only API-key generator for the local-v1 HTTPS listener.
//!
//! This is an explicit Cargo binary, not a server route. The raw token is
//! printed once and is never persisted by this command.

use std::fmt::Write as _;
use std::io::{self, Write as _};
use std::process::ExitCode;

use base64::Engine as _;
use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use serde_json::json;
use sha2::{Digest, Sha256};
use zeroize::Zeroize;

const VERIFIER_DOMAIN: &[u8] = b"TB-APIKEY-V1\0";

fn main() -> ExitCode {
    match generate_and_print() {
        Ok(()) => ExitCode::SUCCESS,
        Err(message) => {
            eprintln!("terrabase-keygen: {message}");
            ExitCode::FAILURE
        }
    }
}

fn generate_and_print() -> Result<(), &'static str> {
    let mut id = [0_u8; 16];
    let mut secret = [0_u8; 32];
    getrandom::fill(&mut id).map_err(|_| "operating-system randomness unavailable")?;
    if getrandom::fill(&mut secret).is_err() {
        secret.zeroize();
        return Err("operating-system randomness unavailable");
    }
    let (mut token, verifier) = render(id, &secret);
    secret.zeroize();

    let entry = json!({
        "id": URL_SAFE_NO_PAD.encode(id),
        "verifier_sha256": verifier,
        "tenant": "<TENANT_ULID>",
        "database": "<DATABASE_ULID>",
        "shard": "<SHARD_DECIMAL>",
        "collections": ["<COLLECTION_ULID>"],
        "permissions": ["read", "write"],
        "revoked": false
    });
    let mut output = io::stdout().lock();
    let result = writeln!(output, "API key (copy once): {token}")
        .and_then(|()| writeln!(output, "Verifier entry template:"))
        .and_then(|()| writeln!(output, "{entry}"))
        .and_then(|()| output.flush());
    token.zeroize();
    result.map_err(|_| "could not write operator output")
}

fn render(id: [u8; 16], secret: &[u8; 32]) -> (String, String) {
    let token = format!(
        "tbk1.{}.{}",
        URL_SAFE_NO_PAD.encode(id),
        URL_SAFE_NO_PAD.encode(secret)
    );
    let mut hasher = Sha256::new();
    hasher.update(VERIFIER_DOMAIN);
    hasher.update(id);
    hasher.update(secret);
    let digest = hasher.finalize();
    let mut verifier = String::with_capacity(64);
    for byte in digest {
        write!(&mut verifier, "{byte:02x}").expect("writing to a String cannot fail");
    }
    (token, verifier)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn token_and_verifier_use_the_fixed_v1_domain_and_lengths() {
        let (token, verifier) = render([3_u8; 16], &[7_u8; 32]);
        let parts: Vec<_> = token.split('.').collect();
        assert_eq!(parts.len(), 3);
        assert_eq!(parts[0], "tbk1");
        assert_eq!(parts[1].len(), 22);
        assert_eq!(parts[2].len(), 43);
        assert_eq!(verifier.len(), 64);
        assert!(
            verifier
                .bytes()
                .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
        );
    }
}
