//! The fixed hash identity of bucket derivation (SH-02 decision).
//!
//! V1 derives a document's bucket as `XXHash64(lowercased document id)`; the
//! bucket value itself is not observable parity, but its fixity is a
//! contract, so the engine carries its own deterministic, safe-Rust XXH64
//! with the seed fixed at zero rather than depending on a hasher whose
//! output may change between toolchain versions. The implementation follows
//! the published xxHash XXH64 specification (BSD-licensed reference by
//! Yann Collet); `src/sharding/tests.rs` anchors the digests to
//! reference-implementation vectors so the identity cannot drift.

/// XXH64 prime 1.
const PRIME_1: u64 = 0x9E37_79B1_85EB_CA87;
/// XXH64 prime 2.
const PRIME_2: u64 = 0xC2B2_AE3D_27D4_EB4F;
/// XXH64 prime 3.
const PRIME_3: u64 = 0x1656_67B1_9E37_79F9;
/// XXH64 prime 4.
const PRIME_4: u64 = 0x85EB_CA77_C2B2_AE63;
/// XXH64 prime 5.
const PRIME_5: u64 = 0x27D4_EB2F_1656_67C5;

/// One 32-byte chunk of input consumed per round loop.
const CHUNK_SIZE: usize = 32;

/// Reads eight bytes as a little-endian `u64`.
///
/// # Panics
///
/// Panics when fewer than eight bytes remain; every caller slices a full
/// chunk first.
fn read_u64_le(bytes: &[u8]) -> u64 {
    u64::from_le_bytes(bytes[..8].try_into().expect("eight bytes remain"))
}

/// Reads four bytes as a little-endian `u32`.
///
/// # Panics
///
/// Panics when fewer than four bytes remain; every caller slices a full
/// word first.
fn read_u32_le(bytes: &[u8]) -> u32 {
    u32::from_le_bytes(bytes[..4].try_into().expect("four bytes remain"))
}

/// One accumulator round over an input word.
const fn round(acc: u64, input: u64) -> u64 {
    acc.wrapping_add(input.wrapping_mul(PRIME_2)).rotate_left(31).wrapping_mul(PRIME_1)
}

/// One merge round of a lane accumulator into the result.
const fn merge_round(mut acc: u64, val: u64) -> u64 {
    acc ^= round(0, val);
    acc.wrapping_mul(PRIME_1).wrapping_add(PRIME_4)
}

/// The final avalanche.
const fn avalanche(mut input: u64) -> u64 {
    input ^= input >> 33;
    input = input.wrapping_mul(PRIME_2);
    input ^= input >> 29;
    input = input.wrapping_mul(PRIME_3);
    input ^= input >> 32;
    input
}

/// Returns the XXH64 digest of `input` with the seed fixed at zero.
///
/// This is the whole hash identity of sharding: same bytes in, same digest
/// out, forever. Bucket derivation reduces this value into
/// [`BUCKET_SPACE`](crate::sharding::BUCKET_SPACE).
pub(crate) fn xxh64(input: &[u8]) -> u64 {
    let total = input.len();
    let mut lanes = [PRIME_1.wrapping_add(PRIME_2), PRIME_2, 0, 0u64.wrapping_sub(PRIME_1)];
    let mut result;

    let mut rest = input;
    if total >= CHUNK_SIZE {
        while rest.len() >= CHUNK_SIZE {
            lanes[0] = round(lanes[0], read_u64_le(&rest[0..]));
            lanes[1] = round(lanes[1], read_u64_le(&rest[8..]));
            lanes[2] = round(lanes[2], read_u64_le(&rest[16..]));
            lanes[3] = round(lanes[3], read_u64_le(&rest[24..]));
            rest = &rest[CHUNK_SIZE..];
        }
        result = lanes[0]
            .rotate_left(1)
            .wrapping_add(lanes[1].rotate_left(7))
            .wrapping_add(lanes[2].rotate_left(12))
            .wrapping_add(lanes[3].rotate_left(18));
        for lane in lanes {
            result = merge_round(result, lane);
        }
    } else {
        result = PRIME_5;
    }

    result = result.wrapping_add(total as u64);

    while rest.len() >= 8 {
        result ^= round(0, read_u64_le(rest));
        result = result.rotate_left(27).wrapping_mul(PRIME_1).wrapping_add(PRIME_4);
        rest = &rest[8..];
    }
    while rest.len() >= 4 {
        result ^= u64::from(read_u32_le(rest)).wrapping_mul(PRIME_1);
        result = result.rotate_left(23).wrapping_mul(PRIME_2).wrapping_add(PRIME_3);
        rest = &rest[4..];
    }
    for byte in rest {
        result ^= u64::from(*byte).wrapping_mul(PRIME_5);
        result = result.rotate_left(11).wrapping_mul(PRIME_1);
    }
    avalanche(result)
}
