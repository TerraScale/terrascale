//! The migration-side SHA-256 digest.
//!
//! The backup core keeps its own digest implementation private to
//! `src/backup` by the W4 contract, and the migration module may not reach
//! into it, so this module carries the representation format's digest
//! arithmetic: FIPS 180-4 SHA-256 over little-endian length-prefixed
//! framing inputs. It is exercised against the FIPS test vectors in its
//! unit tests, and the representation identity — the digest of all
//! manifest lines except the identity line — is derived from it.

/// The SHA-256 round-constant table (first 32 bits of the cube roots of
/// the first 64 primes).
const ROUND_CONSTANTS: [u32; 64] = [
    0x428a_2f98,
    0x7137_4491,
    0xb5c0_fbcf,
    0xe9b5_dba5,
    0x3956_c25b,
    0x59f1_11f1,
    0x923f_82a4,
    0xab1c_5ed5,
    0xd807_aa98,
    0x1283_5b01,
    0x2431_85be,
    0x550c_7dc3,
    0x72be_5d74,
    0x80de_b1fe,
    0x9bdc_06a7,
    0xc19b_f174,
    0xe49b_69c1,
    0xefbe_4786,
    0x0fc1_9dc6,
    0x240c_a1cc,
    0x2de9_2c6f,
    0x4a74_84aa,
    0x5cb0_a9dc,
    0x76f9_88da,
    0x983e_5152,
    0xa831_c66d,
    0xb003_27c8,
    0xbf59_7fc7,
    0xc6e0_0bf3,
    0xd5a7_9147,
    0x06ca_6351,
    0x1429_2967,
    0x27b7_0a85,
    0x2e1b_2138,
    0x4d2c_6dfc,
    0x5338_0d13,
    0x650a_7354,
    0x766a_0abb,
    0x81c2_c92e,
    0x9272_2c85,
    0xa2bf_e8a1,
    0xa81a_664b,
    0xc24b_8b70,
    0xc76c_51a3,
    0xd192_e819,
    0xd699_0624,
    0xf40e_3585,
    0x106a_a070,
    0x19a4_c116,
    0x1e37_6c08,
    0x2748_774c,
    0x34b0_bcb5,
    0x391c_0cb3,
    0x4ed8_aa4a,
    0x5b9c_ca4f,
    0x682e_6ff3,
    0x748f_82ee,
    0x78a5_636f,
    0x84c8_7814,
    0x8cc7_0208,
    0x90be_fffa,
    0xa450_6ceb,
    0xbef9_a3f7,
    0xc671_78f2,
];

/// The initial hash words (first 32 bits of the square roots of the first
/// eight primes).
const INITIAL_HASH: [u32; 8] = [
    0x6a09_e667,
    0xbb67_ae85,
    0x3c6e_f372,
    0xa54f_f53a,
    0x510e_527f,
    0x9b05_688c,
    0x1f83_d9ab,
    0x5be0_cd19,
];

/// An incremental SHA-256 hasher.
pub(super) struct Sha256 {
    state: [u32; 8],
    buffer: [u8; 64],
    buffered: usize,
    length: u64,
}

impl Sha256 {
    /// Creates a hasher at its initial state.
    pub(super) fn new() -> Self {
        Self { state: INITIAL_HASH, buffer: [0u8; 64], buffered: 0, length: 0 }
    }

    /// Absorbs `chunk` into the hash state.
    pub(super) fn update(&mut self, chunk: &[u8]) {
        self.length = self.length.wrapping_add(chunk.len() as u64);
        let mut remaining = chunk;
        if self.buffered > 0 {
            let take = (64 - self.buffered).min(remaining.len());
            self.buffer[self.buffered..self.buffered + take].copy_from_slice(&remaining[..take]);
            self.buffered += take;
            remaining = &remaining[take..];
            if self.buffered == 64 {
                let block = self.buffer;
                self.compress(&block);
                self.buffered = 0;
            }
        }
        while remaining.len() >= 64 {
            let mut block = [0u8; 64];
            block.copy_from_slice(&remaining[..64]);
            self.compress(&block);
            remaining = &remaining[64..];
        }
        if !remaining.is_empty() {
            self.buffer[..remaining.len()].copy_from_slice(remaining);
            self.buffered = remaining.len();
        }
    }

    /// Finishes the hash and returns the 32-byte digest.
    pub(super) fn finish(mut self) -> [u8; 32] {
        let bit_length = self.length.wrapping_mul(8);
        // Padding: one set bit, zeros, then the 64-bit big-endian bit length.
        self.update(&[0x80]);
        while self.buffered != 56 {
            self.update(&[0x00]);
        }
        self.length = 0; // the length word is appended directly, not hashed
        let mut block = self.buffer;
        block[56..64].copy_from_slice(&bit_length.to_be_bytes());
        self.compress(&block);
        let mut digest = [0u8; 32];
        for (index, word) in self.state.iter().enumerate() {
            digest[index * 4..index * 4 + 4].copy_from_slice(&word.to_be_bytes());
        }
        digest
    }

    /// Compresses one 64-byte block into the state.
    #[allow(
        clippy::many_single_char_names,
        reason = "FIPS 180-4 names the compression working words a through h"
    )]
    fn compress(&mut self, block: &[u8; 64]) {
        let mut w = [0u32; 64];
        for (index, word) in w.iter_mut().take(16).enumerate() {
            *word = u32::from_be_bytes([
                block[index * 4],
                block[index * 4 + 1],
                block[index * 4 + 2],
                block[index * 4 + 3],
            ]);
        }
        for index in 16..64 {
            let s0 = w[index - 15].rotate_right(7)
                ^ w[index - 15].rotate_right(18)
                ^ (w[index - 15] >> 3);
            let s1 = w[index - 2].rotate_right(17)
                ^ w[index - 2].rotate_right(19)
                ^ (w[index - 2] >> 10);
            w[index] = w[index - 16].wrapping_add(s0).wrapping_add(w[index - 7]).wrapping_add(s1);
        }
        let [mut a, mut b, mut c, mut d, mut e, mut f, mut g, mut h] = self.state;
        for index in 0..64 {
            let s1 = e.rotate_right(6) ^ e.rotate_right(11) ^ e.rotate_right(25);
            let choice = (e & f) ^ ((!e) & g);
            let temp1 = h
                .wrapping_add(s1)
                .wrapping_add(choice)
                .wrapping_add(ROUND_CONSTANTS[index])
                .wrapping_add(w[index]);
            let s0 = a.rotate_right(2) ^ a.rotate_right(13) ^ a.rotate_right(22);
            let majority = (a & b) ^ (a & c) ^ (b & c);
            let temp2 = s0.wrapping_add(majority);
            h = g;
            g = f;
            f = e;
            e = d.wrapping_add(temp1);
            d = c;
            c = b;
            b = a;
            a = temp1.wrapping_add(temp2);
        }
        self.state[0] = self.state[0].wrapping_add(a);
        self.state[1] = self.state[1].wrapping_add(b);
        self.state[2] = self.state[2].wrapping_add(c);
        self.state[3] = self.state[3].wrapping_add(d);
        self.state[4] = self.state[4].wrapping_add(e);
        self.state[5] = self.state[5].wrapping_add(f);
        self.state[6] = self.state[6].wrapping_add(g);
        self.state[7] = self.state[7].wrapping_add(h);
    }
}

/// Renders a digest as lowercase hex.
pub(super) fn to_hex(digest: &[u8]) -> String {
    let mut text = String::with_capacity(digest.len() * 2);
    for byte in digest {
        text.push(
            char::from_digit(u32::from(byte >> 4), 16).expect("a nibble maps to a hex digit"),
        );
        text.push(
            char::from_digit(u32::from(byte & 0x0F), 16).expect("a nibble maps to a hex digit"),
        );
    }
    text
}

/// Digests one file's bytes without holding the file in memory.
///
/// # Errors
///
/// Returns [`std::io::Error`] when the file cannot be opened or read.
pub(super) fn digest_file(path: &std::path::Path) -> std::io::Result<String> {
    use std::io::Read;
    let mut file = std::fs::File::open(path)?;
    let mut hasher = Sha256::new();
    let mut buffer = [0u8; 8192];
    loop {
        let read = file.read(&mut buffer)?;
        if read == 0 {
            break;
        }
        hasher.update(&buffer[..read]);
    }
    Ok(to_hex(&hasher.finish()))
}

#[cfg(test)]
mod digest_tests {
    use super::{Sha256, to_hex};

    #[test]
    fn empty_input_matches_the_fips_vector() {
        let digest = Sha256::new().finish();
        assert_eq!(
            to_hex(&digest),
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        );
    }

    #[test]
    fn abc_matches_the_fips_vector() {
        let mut hasher = Sha256::new();
        hasher.update(b"abc");
        assert_eq!(
            to_hex(&hasher.finish()),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
    }

    #[test]
    fn chunked_updates_match_one_shot_updates() {
        #[allow(clippy::cast_possible_truncation)]
        let payload: Vec<u8> = (0..1000u32).map(|value| value as u8).collect();
        let mut one_shot = Sha256::new();
        one_shot.update(&payload);
        let mut chunked = Sha256::new();
        for chunk in payload.chunks(7) {
            chunked.update(chunk);
        }
        assert_eq!(to_hex(&one_shot.finish()), to_hex(&chunked.finish()));
    }
}
