use hmac::{Hmac, Mac};
use rand::{Rng, rngs::OsRng};
use sha2::{Digest, Sha256};
use subtle::ConstantTimeEq;

const ALPHABET: &[u8] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

pub fn random_token(length: usize) -> String {
    let mut rng = OsRng;
    (0..length)
        .map(|_| ALPHABET[rng.gen_range(0..ALPHABET.len())] as char)
        .collect()
}

pub fn random_bytes<const N: usize>() -> [u8; N] {
    let mut bytes = [0_u8; N];
    rand::RngCore::fill_bytes(&mut OsRng, &mut bytes);
    bytes
}

pub fn random_digits(length: u32) -> Option<String> {
    if !(1..=9).contains(&length) {
        return None;
    }
    let upper = 10_u32.pow(length);
    let value = OsRng.gen_range(0..upper);
    Some(format!("{:0width$}", value, width = length as usize))
}

pub fn new_secret(prefix: &str) -> (String, String, String) {
    let id = format!("{prefix}_{}", random_token(22));
    let secret = random_token(40);
    let combined = format!("{id}_{secret}");
    (id, secret, combined)
}

pub fn sha256_hex(value: &str) -> String {
    hex::encode(Sha256::digest(value.as_bytes()))
}

pub fn fixed_time_equals(left: &str, right: &str) -> bool {
    left.as_bytes().ct_eq(right.as_bytes()).into()
}

/// # Panics
///
/// This can only panic if the HMAC implementation rejects a key length it
/// documents as valid (HMAC accepts keys of any length).
pub fn hmac_sha256(key: &[u8], message: &[u8]) -> [u8; 32] {
    let mut mac = Hmac::<Sha256>::new_from_slice(key).expect("HMAC accepts any key length");
    mac.update(message);
    mac.finalize().into_bytes().into()
}

pub fn verify_hmac_sha256(key: &[u8], message: &[u8], signature: &[u8]) -> bool {
    bool::from(hmac_sha256(key, message).as_slice().ct_eq(signature))
}
