use std::collections::HashSet;
use std::thread;
use std::time::{Duration, UNIX_EPOCH};

use super::{ULID_BINARY_BYTES, UlidError, compare_storage_keys, from_binary, from_text, generate};

const EXAMPLE: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

#[test]
fn canonical_vectors_round_trip() {
    let minimum = from_text("00000000000000000000000000").unwrap();
    let maximum = from_text("7ZZZZZZZZZZZZZZZZZZZZZZZZZ").unwrap();
    let example = from_text(EXAMPLE).unwrap();
    assert_eq!(minimum, ::ulid::Ulid::from_bytes([0; 16]));
    assert_eq!(maximum, ::ulid::Ulid::from_bytes([0xff; 16]));
    assert_eq!(example.to_string(), EXAMPLE);
    assert_eq!(
        example.to_bytes(),
        [1, 86, 62, 58, 181, 211, 214, 118, 76, 97, 239, 185, 147, 2, 189, 91]
    );
    assert_eq!(example.timestamp_ms(), 1_469_922_850_259);
    assert_eq!(example.random(), 1_012_768_647_078_601_740_696_923);
    assert_eq!(example.to_bytes().len(), ULID_BINARY_BYTES);
    assert_eq!(from_binary(&example.to_bytes()), Ok(example));
}

#[test]
fn strict_parser_rejects_noncanonical_text() {
    for (text, length) in [
        ("", 0),
        ("01ARZ3NDEKTSV4RRFFQ69G5FA", 25),
        ("01ARZ3NDEKTSV4RRFFQ69G5FAVX", 27),
        ("{01ARZ3NDEKTSV4RRFFQ69G5FAV}", 28),
    ] {
        assert_eq!(from_text(text), Err(UlidError::WrongTextLength(length)));
    }
    assert_eq!(
        from_text("01arz3ndektsv4rrffq69g5fav"),
        Err(UlidError::NonCanonicalCharacter { index: 2, byte: b'a' })
    );
    for character in [b'I', b'L', b'O', b'U', b'i', b'l', b'o', b'u'] {
        let mut text = EXAMPLE.as_bytes().to_vec();
        text[25] = character;
        let text = String::from_utf8(text).unwrap();
        assert_eq!(
            from_text(&text),
            Err(UlidError::AmbiguousCharacter { index: 25, byte: character })
        );
    }
    for first in [b'8', b'9', b'A', b'Z'] {
        let mut text = EXAMPLE.as_bytes().to_vec();
        text[0] = first;
        let text = String::from_utf8(text).unwrap();
        assert_eq!(from_text(&text), Err(UlidError::TextOverflow { first }));
    }
    for length in [0, 15, 17, 32] {
        assert_eq!(from_binary(&vec![0; length]), Err(UlidError::WrongBinaryLength(length)));
    }
}

#[test]
fn binary_storage_keys_preserve_ulid_order() {
    let first = ::ulid::Ulid::from(1_u128 << 80 | 1);
    let second = ::ulid::Ulid::from(1_u128 << 80 | 2);
    assert!(first < second);
    assert_eq!(
        compare_storage_keys(&first.to_bytes(), &second.to_bytes()),
        std::cmp::Ordering::Less
    );
}

#[test]
fn process_generator_is_safe_for_concurrent_callers() {
    let threads: Vec<_> = (0..8)
        .map(|_| thread::spawn(|| (0..128).map(|_| generate()).collect::<Vec<_>>()))
        .collect();
    let values: Vec<_> = threads
        .into_iter()
        .flat_map(|thread| {
            let values = thread.join().unwrap();
            assert!(values.windows(2).all(|window| window[0] < window[1]));
            values
        })
        .collect();
    let unique: HashSet<_> = values.iter().copied().collect();
    assert_eq!(unique.len(), values.len());
}

#[test]
fn upstream_generator_handles_fixed_and_backward_times() {
    let mut generator = ::ulid::Generator::new();
    let fixed = UNIX_EPOCH + Duration::from_millis(1_700_000_000_000);
    let earlier = fixed - Duration::from_millis(1);
    let first = generator.generate_from_datetime(fixed).unwrap();
    let second = generator.generate_from_datetime(fixed).unwrap();
    let third = generator.generate_from_datetime(earlier).unwrap();
    assert!(second > first);
    assert!(third > second);
}

#[test]
fn typed_entity_ids_remain_distinct() {
    let bytes = [7; ULID_BINARY_BYTES];
    let cluster = super::ClusterUlid::from_wire_bytes(bytes);
    let document = super::DocumentUlid::from_wire_bytes(bytes);
    assert_eq!(cluster.as_bytes(), document.as_bytes());
    assert_eq!(super::ClusterUlid::from_binary(&bytes), Ok(cluster));
    assert_eq!(super::DocumentUlid::from_binary(&bytes), Ok(document));
}

#[test]
fn typed_ids_use_shared_generation_and_strict_boundaries() {
    let first = generate();
    let document = super::DocumentUlid::generate();
    let last = generate();
    assert!(first < document.value());
    assert!(document.value() < last);
    assert_eq!(super::DocumentUlid::from_text(&document.to_text()), Ok(document));
    assert!(super::DocumentUlid::from_text(&EXAMPLE.to_lowercase()).is_err());
    assert!(super::DocumentUlid::from_binary(&[0; 17]).is_err());
    let mut buffer = [0; super::ULID_TEXT_LENGTH];
    assert_eq!(&*document.write_text(&mut buffer), document.to_text());
    let exhausted = ::ulid::Ulid::from_parts(1_700_000_000_000, super::MAX_ENTROPY);
    assert_eq!(exhausted.increment(), None);
}
