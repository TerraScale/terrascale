use hmac::{Hmac, Mac};
use sha1::Sha1;
use sha2::{Sha256, Sha512};
use subtle::ConstantTimeEq;

#[derive(Debug, Clone, Copy)]
pub enum Algorithm {
    Sha1,
    Sha256,
    Sha512,
}

fn mac(secret: &[u8], counter: u64, algorithm: Algorithm) -> Vec<u8> {
    let input = counter.to_be_bytes();
    match algorithm {
        Algorithm::Sha1 => {
            let mut h = Hmac::<Sha1>::new_from_slice(secret).expect("HMAC accepts any key");
            h.update(&input);
            h.finalize().into_bytes().to_vec()
        }
        Algorithm::Sha256 => {
            let mut h = Hmac::<Sha256>::new_from_slice(secret).expect("HMAC accepts any key");
            h.update(&input);
            h.finalize().into_bytes().to_vec()
        }
        Algorithm::Sha512 => {
            let mut h = Hmac::<Sha512>::new_from_slice(secret).expect("HMAC accepts any key");
            h.update(&input);
            h.finalize().into_bytes().to_vec()
        }
    }
}

pub fn hotp(secret: &[u8], counter: u64, digits: u32, algorithm: Algorithm) -> Option<String> {
    if !(1..=9).contains(&digits) {
        return None;
    }
    let digest = mac(secret, counter, algorithm);
    let offset = usize::from(digest[digest.len() - 1] & 0x0f);
    let binary = (u32::from(digest[offset] & 0x7f) << 24)
        | (u32::from(digest[offset + 1]) << 16)
        | (u32::from(digest[offset + 2]) << 8)
        | u32::from(digest[offset + 3]);
    Some(format!(
        "{:0width$}",
        binary % 10_u32.pow(digits),
        width = digits as usize
    ))
}

pub fn compute(secret: &[u8], unix_seconds: i64) -> Option<String> {
    let counter = u64::try_from(unix_seconds.div_euclid(30)).ok()?;
    hotp(secret, counter, 6, Algorithm::Sha1)
}

pub fn verify(secret: &[u8], code: &str, unix_seconds: i64) -> bool {
    verify_with_options(secret, code, unix_seconds, 1, 6, 30, Algorithm::Sha1)
}

pub fn verify_with_options(
    secret: &[u8],
    code: &str,
    unix_seconds: i64,
    window: i64,
    digits: u32,
    step_seconds: i64,
    algorithm: Algorithm,
) -> bool {
    if step_seconds <= 0
        || !(0..=100).contains(&window)
        || code.len() != digits as usize
        || !code.as_bytes().iter().all(u8::is_ascii_digit)
    {
        return false;
    }
    let step = unix_seconds.div_euclid(step_seconds);
    (-window..=window).any(|offset| {
        let Some(counter) = step.checked_add(offset).and_then(|x| u64::try_from(x).ok()) else {
            return false;
        };
        hotp(secret, counter, digits, algorithm)
            .is_some_and(|candidate| bool::from(candidate.as_bytes().ct_eq(code.as_bytes())))
    })
}
