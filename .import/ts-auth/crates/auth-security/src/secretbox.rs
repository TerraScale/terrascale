use aes_gcm::{Aes256Gcm, KeyInit, Nonce, aead::Aead};
use base64::{Engine, engine::general_purpose::STANDARD};
use rand::{RngCore, rngs::OsRng};

#[derive(Debug, thiserror::Error)]
pub enum SecretBoxError {
    #[error("master key must be 32 bytes")]
    InvalidKey,
    #[error("unknown secret box version")]
    Version,
    #[error("truncated or malformed secret box")]
    Format,
    #[error("secret box authentication failed")]
    Authentication,
}

/// C# persisted layout: `v1:` + base64(nonce[12] || ciphertext || tag[16]).
pub fn encrypt(master_key: &[u8], plaintext: &[u8]) -> Result<String, SecretBoxError> {
    let cipher = Aes256Gcm::new_from_slice(master_key).map_err(|_| SecretBoxError::InvalidKey)?;
    let mut nonce = [0_u8; 12];
    OsRng.fill_bytes(&mut nonce);
    let ciphertext_and_tag = cipher
        .encrypt(&Nonce::from(nonce), plaintext)
        .map_err(|_| SecretBoxError::Authentication)?;
    let mut raw = Vec::with_capacity(12 + ciphertext_and_tag.len());
    raw.extend_from_slice(&nonce);
    raw.extend_from_slice(&ciphertext_and_tag);
    Ok(format!("v1:{}", STANDARD.encode(raw)))
}

pub fn decrypt(master_key: &[u8], boxed: &str) -> Result<Vec<u8>, SecretBoxError> {
    let cipher = Aes256Gcm::new_from_slice(master_key).map_err(|_| SecretBoxError::InvalidKey)?;
    let encoded = boxed.strip_prefix("v1:").ok_or(SecretBoxError::Version)?;
    let raw = STANDARD
        .decode(encoded)
        .map_err(|_| SecretBoxError::Format)?;
    if raw.len() < 28 {
        return Err(SecretBoxError::Format);
    }
    let nonce: [u8; 12] = raw[..12].try_into().map_err(|_| SecretBoxError::Format)?;
    cipher
        .decrypt(&Nonce::from(nonce), &raw[12..])
        .map_err(|_| SecretBoxError::Authentication)
}
