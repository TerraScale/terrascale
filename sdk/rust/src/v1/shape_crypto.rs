//! TBS1 sealed shape objects (`shape-reads.md` section 12).
//!
//! [`decrypt`] authenticates the whole object before it returns any
//! plaintext, so a rejected object contributes nothing to the caller.

use std::ops::Range;

use ring::{aead, hkdf};

use super::{Error, Result};

const MAGIC: &[u8; 4] = b"TBS1";
const EPOCH: Range<usize> = 4..12;
const KEY_ID: Range<usize> = 12..38;
const SALT: Range<usize> = 38..70;
const HEADER_LEN: usize = 70;
const LENGTH_WORD_LEN: usize = 4;
const TAG_LEN: usize = 16;
const FRAME_LEN: usize = 1 << 20;
const MAX_PLAINTEXT_LEN: usize = 8 << 20;
const LAST_FRAME: u32 = 1 << 31;

/// Decrypts one TBS1 object for `handle` at `request_offset`.
///
/// `epoch_key` is the `K_e` released for `epoch` and `key_id`. Every check
/// and tag runs before this returns, so the caller applies nothing from a
/// rejected object.
pub(crate) fn decrypt(
    object: &[u8],
    handle: &str,
    request_offset: &str,
    epoch: u64,
    key_id: &str,
    epoch_key: &[u8; 32],
) -> Result<Vec<u8>> {
    let header = object
        .get(..HEADER_LEN)
        .ok_or_else(|| reject("short header"))?;
    if &header[..4] != MAGIC {
        return Err(reject("bad magic"));
    }
    if header[EPOCH] != epoch.to_be_bytes() {
        return Err(reject("epoch mismatch"));
    }
    if header[KEY_ID] != *key_id.as_bytes() {
        return Err(reject("key id mismatch"));
    }

    let object_key = zeroize::Zeroizing::new(object_key(
        epoch_key,
        &header[SALT],
        &binding(handle, request_offset),
    )?);
    let key = aead::LessSafeKey::new(
        aead::UnboundKey::new(&aead::AES_256_GCM, &object_key[..]).map_err(|_| reject("key"))?,
    );

    let mut plaintext = Vec::new();
    let mut pos = HEADER_LEN;
    let mut index = 0u64;
    loop {
        let word_bytes: [u8; LENGTH_WORD_LEN] = object
            .get(pos..pos + LENGTH_WORD_LEN)
            .and_then(|bytes| bytes.try_into().ok())
            .ok_or_else(|| reject("truncated frame"))?;
        let word = u32::from_be_bytes(word_bytes);
        let last = word & LAST_FRAME != 0;
        let len = (word & !LAST_FRAME) as usize;
        if len > FRAME_LEN {
            return Err(reject("oversized frame"));
        }
        if last && len == FRAME_LEN {
            return Err(reject("full frame marked last"));
        }
        if !last && len != FRAME_LEN {
            return Err(reject("short frame not last"));
        }
        if plaintext.len() + len > MAX_PLAINTEXT_LEN {
            return Err(reject("plaintext too large"));
        }

        let body_start = pos + LENGTH_WORD_LEN;
        let body_end = body_start + len + TAG_LEN;
        let body = object
            .get(body_start..body_end)
            .ok_or_else(|| reject("truncated frame"))?;
        let mut in_out = body.to_vec();
        let aad = frame_aad(header, index, word);
        let opened = key
            .open_in_place(
                aead::Nonce::assume_unique_for_key(frame_nonce(index)),
                aead::Aad::from(aad.as_slice()),
                &mut in_out,
            )
            .map_err(|_| reject("authentication failed"))?;
        plaintext.extend_from_slice(opened);

        pos = body_end;
        index += 1;
        if last {
            break;
        }
    }
    if pos != object.len() {
        return Err(reject("trailing bytes"));
    }
    Ok(plaintext)
}

/// `handle`, a NUL, then the canonical request offset.
fn binding(handle: &str, request_offset: &str) -> Vec<u8> {
    let mut binding = handle.as_bytes().to_vec();
    binding.push(0);
    binding.extend_from_slice(request_offset.as_bytes());
    binding
}

fn object_key(epoch_key: &[u8; 32], salt: &[u8], binding: &[u8]) -> Result<[u8; 32]> {
    let mut key = [0u8; 32];
    hkdf::Salt::new(hkdf::HKDF_SHA256, salt)
        .extract(epoch_key)
        .expand(&[&b"TBS1 object\0"[..], binding], hkdf::HKDF_SHA256)
        .and_then(|okm| okm.fill(&mut key))
        .map_err(|_| reject("hkdf"))?;
    Ok(key)
}

fn frame_nonce(index: u64) -> [u8; 12] {
    let mut nonce = [0u8; 12];
    nonce[4..].copy_from_slice(&index.to_be_bytes());
    nonce
}

fn frame_aad(header: &[u8], index: u64, word: u32) -> Vec<u8> {
    let mut aad = header.to_vec();
    aad.extend_from_slice(&index.to_be_bytes());
    aad.extend_from_slice(&word.to_be_bytes());
    aad
}

fn reject(reason: &str) -> Error {
    Error::Decode(format!("TBS1 object rejected: {reason}"))
}
