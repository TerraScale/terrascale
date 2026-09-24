//! `BLAKE2b` with an explicit digest size, as used by `Argon2id`.

use blake2::{
    Blake2bVar,
    digest::{Update, VariableOutput},
};

#[derive(Debug, thiserror::Error)]
#[error("BLAKE2b digest length must be between 1 and 64 bytes")]
pub struct InvalidDigestLength;

pub struct Blake2b(Blake2bVar);

impl Blake2b {
    pub fn new(digest_length: usize) -> Result<Self, InvalidDigestLength> {
        if !(1..=64).contains(&digest_length) {
            return Err(InvalidDigestLength);
        }
        Blake2bVar::new(digest_length)
            .map(Self)
            .map_err(|_| InvalidDigestLength)
    }

    pub fn update(&mut self, bytes: &[u8]) {
        self.0.update(bytes);
    }

    /// # Panics
    ///
    /// This can only panic if the hash library rejects the same output size it
    /// accepted when constructing this instance.
    pub fn finalize(self) -> Vec<u8> {
        let mut output = vec![0; self.0.output_size()];
        // The output is allocated at exactly the size accepted at construction.
        self.0
            .finalize_variable(&mut output)
            .expect("valid BLAKE2b output size");
        output
    }
}

pub fn hash(digest_length: usize, bytes: &[u8]) -> Result<Vec<u8>, InvalidDigestLength> {
    let mut hasher = Blake2b::new(digest_length)?;
    hasher.update(bytes);
    Ok(hasher.finalize())
}
