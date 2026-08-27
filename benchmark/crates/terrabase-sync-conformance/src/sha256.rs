//! A minimal, dependency-free SHA-256 (FIPS 180-4) for the corpus
//! integrity manifest. Vendored on purpose — exactly like the engine
//! core's `src/backup/sha256.rs` — so the conformance kit's dependency
//! closure stays at `serde_json` (the corpus format) plus the delivery
//! core, and the integrator's workspace wiring moves no new packages.
//!
//! Not a claim about speed; the manifest digests nine corpus files.

/// The SHA-256 compression constants (FIPS 180-4 section 4.2.2).
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

/// The streaming SHA-256 hasher.
pub(super) struct Sha256 {
    state: [u32; 8],
    buffer: [u8; 64],
    buffered: usize,
    length: u64,
}

impl Default for Sha256 {
    fn default() -> Self {
        Self::new()
    }
}

impl Sha256 {
    /// A fresh hasher at the standard initial state (FIPS 180-4 5.3.3).
    pub(super) fn new() -> Self {
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
            buffered: 0,
            length: 0,
        }
    }

    /// Absorbs bytes.
    pub(super) fn update(&mut self, bytes: &[u8]) {
        self.length = self.length.wrapping_add(bytes.len() as u64);
        self.absorb(bytes);
    }

    /// Buffers and compresses bytes without touching the message
    /// length (shared by [`Sha256::update`] and the padding step).
    fn absorb(&mut self, mut rest: &[u8]) {
        if self.buffered > 0 {
            let take = rest.len().min(64 - self.buffered);
            self.buffer[self.buffered..self.buffered + take].copy_from_slice(&rest[..take]);
            self.buffered += take;
            rest = &rest[take..];
            if self.buffered == 64 {
                let block = self.buffer;
                self.compress(&block);
                self.buffered = 0;
            }
        }
        while rest.len() >= 64 {
            let mut block = [0u8; 64];
            block.copy_from_slice(&rest[..64]);
            self.compress(&block);
            rest = &rest[64..];
        }
        if !rest.is_empty() {
            self.buffer[..rest.len()].copy_from_slice(rest);
            self.buffered = rest.len();
        }
    }

    /// Finishes and returns the 32-byte digest.
    pub(super) fn finish(mut self) -> [u8; 32] {
        let bits = self.length.wrapping_mul(8);
        // Pad per FIPS 180-4 5.1.1: 0x80, zeros to the 56-byte mark
        // (of this or the next block), then the big-endian bit length.
        let pad_len = if self.buffered < 56 { 56 - self.buffered } else { 120 - self.buffered };
        let mut pad = vec![0u8; pad_len + 8];
        pad[0] = 0x80;
        pad[pad_len..].copy_from_slice(&bits.to_be_bytes());
        self.absorb(&pad);
        debug_assert_eq!(self.buffered, 0, "padding leaves no partial block");
        let mut digest = [0u8; 32];
        for (index, word) in self.state.iter().enumerate() {
            digest[index * 4..index * 4 + 4].copy_from_slice(&word.to_be_bytes());
        }
        digest
    }

    /// The compression function over one 64-byte block. The
    /// single-character bindings are the FIPS 180-4 variable names.
    #[allow(clippy::many_single_char_names)]
    fn compress(&mut self, block: &[u8; 64]) {
        let mut w = [0u32; 64];
        for index in 0..16 {
            w[index] = u32::from_be_bytes([
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
            let choose = (e & f) ^ ((!e) & g);
            let temp1 = h
                .wrapping_add(s1)
                .wrapping_add(choose)
                .wrapping_add(K[index])
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
        let round = [a, b, c, d, e, f, g, h];
        for (index, word) in round.into_iter().enumerate() {
            self.state[index] = self.state[index].wrapping_add(word);
        }
    }
}

/// Lowercase hex encoding (the manifest's digest representation).
pub(super) fn to_hex(bytes: &[u8]) -> String {
    let mut out = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        use std::fmt::Write as _;
        let _ = write!(out, "{byte:02x}");
    }
    out
}

#[cfg(test)]
mod tests {
    use super::Sha256;
    use super::to_hex;

    fn digest(input: &[u8]) -> String {
        let mut hasher = Sha256::new();
        hasher.update(input);
        to_hex(&hasher.finish())
    }

    #[test]
    fn known_answer_vectors() {
        // FIPS 180-4 examples plus the empty message and a multi-block
        // message that exercises both padding branches.
        assert_eq!(digest(b""), "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        assert_eq!(
            digest(b"abc"),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
        assert_eq!(
            digest(b"abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"),
            "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1"
        );
        assert_eq!(
            digest(&[b'a'; 100]),
            "2816597888e4a0d3a36b82b83316ab32680eb8f00f8cd3b904d681246d285a0e"
        );
    }

    #[test]
    fn streaming_matches_one_shot_regardless_of_chunking() {
        let message: Vec<u8> = (0..500u16).flat_map(u16::to_le_bytes).collect();
        let one_shot = digest(&message);
        for chunk in [1usize, 3, 7, 63, 64, 65, 128] {
            let mut hasher = Sha256::new();
            for piece in message.chunks(chunk) {
                hasher.update(piece);
            }
            assert_eq!(to_hex(&hasher.finish()), one_shot, "chunk size {chunk}");
        }
    }
}
