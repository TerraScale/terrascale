//! Streaming SHA-256 helpers used by backup archives.

use aws_lc_rs::digest::{Context, SHA256};

/// Streams one file through SHA-256 in bounded chunks and returns its
/// digest as lowercase hex.
pub(crate) fn digest_file(path: &std::path::Path) -> std::io::Result<String> {
    use std::io::Read;

    let mut file = std::fs::File::open(path)?;
    let mut hasher = Context::new(&SHA256);
    let mut chunk = vec![0_u8; 64 * 1024];
    loop {
        let read = file.read(&mut chunk)?;
        if read == 0 {
            break;
        }
        hasher.update(&chunk[..read]);
    }
    Ok(hex::encode(hasher.finish().as_ref()))
}

#[cfg(test)]
mod sha_tests {
    use aws_lc_rs::digest::{Context, SHA256};

    fn digest_of(message: &[u8]) -> String {
        let mut hasher = Context::new(&SHA256);
        hasher.update(message);
        hex::encode(hasher.finish().as_ref())
    }

    #[test]
    fn sha256_known_answer_vectors() {
        assert_eq!(
            digest_of(b""),
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        );
        assert_eq!(
            digest_of(b"abc"),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
        assert_eq!(
            digest_of(b"abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"),
            "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1"
        );
    }

    #[test]
    fn sha256_streaming_matches_one_shot_regardless_of_chunking() {
        let message: Vec<u8> = (0..1000_u32)
            .map(|value| u8::try_from(value % 256).expect("below 256 by construction"))
            .collect();
        let one_shot = digest_of(&message);
        for chunk_size in [1_usize, 3, 63, 64, 65, 128, 999, 1000] {
            let mut hasher = Context::new(&SHA256);
            for chunk in message.chunks(chunk_size) {
                hasher.update(chunk);
            }
            assert_eq!(hex::encode(hasher.finish().as_ref()), one_shot, "chunk size {chunk_size}");
        }
    }

    #[test]
    fn sha256_multi_block_message_length_boundaries() {
        for length in [55_usize, 56, 57, 63, 64, 65, 119, 120, 128] {
            let message = vec![b'x'; length];
            let mut hasher = Context::new(&SHA256);
            hasher.update(&message[..length / 2]);
            hasher.update(&message[length / 2..]);
            assert_eq!(
                hex::encode(hasher.finish().as_ref()),
                digest_of(&message),
                "length {length}"
            );
        }
    }
}
