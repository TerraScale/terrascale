//! In-crate SHA-256 (FIPS 180-4) for the attachment content plane (GAP-F01).
//!
//! The workspace constraint stack forbids external dependencies, and the
//! existing implementations — `src/backup/sha256.rs` and
//! `src/families/migrations/digest.rs` — are private to modules this packet
//! does not own. This module mirrors that implementation — streaming,
//! constant-space, no unsafe code — so attachment content digests use the
//! same primitive the backup and migration manifests use. A collision action
//! records the option for the integrator to expose one shared hasher instead
//! of three copies; until then the known-answer tests below pin every copy
//! to FIPS 180-4 vectors.

/// A streaming SHA-256 hasher.
#[derive(Clone, Debug)]
pub(crate) struct Sha256 {
    state: [u32; 8],
    /// Bytes already buffered for the next block, always `< 64`.
    buffer: [u8; 64],
    buffer_len: usize,
    /// Total message length absorbed so far, in bytes.
    length: u64,
}

impl Default for Sha256 {
    fn default() -> Self {
        Self::new()
    }
}

impl Sha256 {
    /// Creates a hasher over the empty message.
    pub(crate) fn new() -> Self {
        Self {
            state: [
                0x6a09_e667,
                0xbb67_ae85,
                0x3c6e_f372,
                0xa54f_f53a,
                0x510e_527f,
                0x9b05_688c,
                0x1f83_d9ab,
                0x5be0_cd19,
            ],
            buffer: [0; 64],
            buffer_len: 0,
            length: 0,
        }
    }

    /// Absorbs one chunk of the message.
    pub(crate) fn update(&mut self, chunk: &[u8]) {
        self.length = self.length.wrapping_add(chunk.len() as u64);
        self.absorb(chunk);
    }

    /// Absorbs bytes into the block buffer without length accounting, so
    /// padding never corrupts the recorded message length.
    fn absorb(&mut self, mut chunk: &[u8]) {
        if self.buffer_len > 0 {
            let take = (64 - self.buffer_len).min(chunk.len());
            self.buffer[self.buffer_len..self.buffer_len + take].copy_from_slice(&chunk[..take]);
            self.buffer_len += take;
            chunk = &chunk[take..];
            if self.buffer_len == 64 {
                let block = self.buffer;
                self.compress(&block);
                self.buffer_len = 0;
            }
        }
        while chunk.len() >= 64 {
            let mut fixed = [0u8; 64];
            fixed.copy_from_slice(&chunk[..64]);
            self.compress(&fixed);
            chunk = &chunk[64..];
        }
        if !chunk.is_empty() {
            self.buffer[..chunk.len()].copy_from_slice(chunk);
            self.buffer_len = chunk.len();
        }
    }

    /// Finishes the message and returns its SHA-256 digest.
    pub(crate) fn finish(mut self) -> [u8; 32] {
        let bit_length = self.length.wrapping_mul(8);
        // Padding: one set bit, zeros to 56 modulo 64, then the message
        // length in bits as a big-endian 64-bit integer.
        self.absorb(&[0x80]);
        while self.buffer_len != 56 {
            self.absorb(&[0x00]);
        }
        self.absorb(&bit_length.to_be_bytes());
        debug_assert_eq!(self.buffer_len, 0, "padding always completes the final block");
        let mut digest = [0u8; 32];
        for (index, word) in self.state.iter().enumerate() {
            digest[index * 4..index * 4 + 4].copy_from_slice(&word.to_be_bytes());
        }
        digest
    }

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
            let ch = (e & f) ^ (!e & g);
            let temp1 =
                h.wrapping_add(s1).wrapping_add(ch).wrapping_add(K[index]).wrapping_add(w[index]);
            let s0 = a.rotate_right(2) ^ a.rotate_right(13) ^ a.rotate_right(22);
            let maj = (a & b) ^ (a & c) ^ (b & c);
            let temp2 = s0.wrapping_add(maj);
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

/// Round constants (first 32 bits of the cube roots of the first 64 primes).
const K: [u32; 64] = [
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

/// Renders a digest as its canonical lowercase hex string.
pub(crate) fn to_hex(digest: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut text = String::with_capacity(digest.len() * 2);
    for byte in digest {
        text.push(HEX[(byte >> 4) as usize] as char);
        text.push(HEX[(byte & 0x0f) as usize] as char);
    }
    text
}

#[cfg(test)]
mod digest_tests {
    use super::{Sha256, to_hex};

    fn digest_of(message: &[u8]) -> String {
        let mut hasher = Sha256::new();
        hasher.update(message);
        to_hex(&hasher.finish())
    }

    #[test]
    fn sha256_known_answer_vectors() {
        // FIPS 180-4 and NIST example vectors.
        assert_eq!(
            digest_of(b"abc"),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
        assert_eq!(
            digest_of(b""),
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        );
        assert_eq!(
            digest_of(b"abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"),
            "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1"
        );
    }

    #[test]
    fn sha256_pad_crossing_final_remainder_known_answers() {
        // Lengths whose final remainder is 57..=63 bytes are the one padding
        // shape the FIPS vectors above never exercise: the 0x80 pad byte and
        // part of the zero padding spill into one extra block before the
        // 64-bit length word, and length 64 is the exact-block boundary. The
        // message of length L is the bytes 0x00..=L-1; every digest below was
        // derived independently of this implementation (Python 3 hashlib,
        // OpenSSL SHA-256) so the vectors are known answers, not self-checks.
        let vectors: [(usize, &str); 8] = [
            (57, "2fe741af801cc238602ac0ec6a7b0c3a8a87c7fc7d7f02a3fe03d1c12eac4d8f"),
            (58, "e03b18640c635b338a92b82cce4ff072f9f1aba9ac5261ee1340f592f35c0499"),
            (59, "bd2de8f5dd15c73f68dfd26a614080c2e323b2b51b1b5ed9d7933e535d223bda"),
            (60, "0ddde28e40838ef6f9853e887f597d6adb5f40eb35d5763c52e1e64d8ba3bfff"),
            (61, "4b5c2783c91ceccb7c839213bcbb6a902d7fe8c2ec866877a51f433ea17f3e85"),
            (62, "c89da82cbcd76ddf220e4e9091019b9866ffda72bee30de1effe6c99701a2221"),
            (63, "29af2686fd53374a36b0846694cc342177e428d1647515f078784d69cdb9e488"),
            (64, "fdeab9acf3710362bd2658cdc9a29e8f9c757fcf9811603a8c447cd1d9151108"),
        ];
        for (len, expected) in vectors {
            let message: Vec<u8> =
                (0..len).map(|byte| u8::try_from(byte).expect("in range")).collect();
            assert_eq!(digest_of(&message), expected, "message length {len}");
        }
    }

    #[test]
    fn sha256_streaming_matches_one_shot_regardless_of_chunking() {
        let message: Vec<u8> =
            (0..1000_usize).map(|index| u8::try_from(index % 256).expect("in range")).collect();
        let one_shot = digest_of(&message);
        for chunk_size in [1, 3, 63, 64, 65, 513] {
            let mut hasher = Sha256::new();
            for chunk in message.chunks(chunk_size) {
                hasher.update(chunk);
            }
            assert_eq!(to_hex(&hasher.finish()), one_shot, "chunk size {chunk_size}");
        }
    }
}
