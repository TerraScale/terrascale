//! Order-preserving key encoding for the `SQLite` auto-index side tables
//! (TB-23 groundwork, decisions D-30/D-31 in `GOAL.md`).
//!
//! The target storage shape is one side table per automatic index,
//! `x_<index>(collection, id, <field>_key BLOB, PRIMARY KEY(collection, id))`
//! with a btree index on `(collection, <field>_key, id)`. Under `SQLite`'s
//! default BINARY collation, a single BLOB column whose byte order mirrors
//! the engine's total document order serves equality, ranges, and
//! `ORDER BY` through one btree.
//!
//! ## The spec this mirrors
//!
//! [`super::model::rank`] and [`super::model::compare_values`] pin the
//! engine's total document order: missing (rank 0) sorts first and is
//! **not encoded** — an absent field is an absent side-table key (SQL
//! `NULL`), never a byte string. Every present value encodes as one rank
//! byte (the ranks of `model.rs`: null 1, boolean 2, integer and finite
//! number 3, non-finite number 4, string 5, array 6, object 7) followed by
//! a payload that orders exactly as `compare_values` orders.
//!
//! ## Per-type scheme
//!
//! - **Null**: rank byte only.
//! - **Boolean**: one byte, `false` 0x00 before `true` 0x01.
//! - **Numbers (rank 3)**: exact mathematical order over `i64` ∪ finite
//!   `f64`. Each value is canonicalized to `odd m · 2^e`; positives emit a
//!   sign byte 0x02, the leading bit's exponent `E` (of `2^E · 1.b1b2…`)
//!   biased by 1074 as two big-endian bytes, then one byte per mantissa
//!   bit below the leading one (0x01 set, 0x00 clear)
//!   terminated by 0x00; negatives mirror that body byte-complemented
//!   behind sign byte 0xFD; zero (i64 0, +0.0, −0.0) is the single byte
//!   0x01, which sorts between the negative and positive sign bytes. This
//!   makes `Integer(n)` and `Number(n as f64)` encode identically and
//!   interleaves both encodings exactly where
//!   `compare_integer_with_number` puts them — the fixed-width IEEE bit
//!   flip cannot do this, because the 2^64 float patterns already exhaust
//!   the 8-byte space the integers also need (e.g. `i64 2^53+1` lies
//!   strictly between the adjacent floats `2^53.0` and `2^53+2.0`).
//!   A terminator sorts below every continuation bit, and a terminated
//!   expansion is mathematically smaller than any extension of it, so
//!   prefix-first byte order is exactly numeric order.
//! - **Non-finite numbers (rank 4)**: the IEEE-754 total-order bit flip of
//!   the eight big-endian `to_bits` bytes (sign-set patterns complemented,
//!   positive patterns sign-bit flipped), which is precisely the order of
//!   `f64::total_cmp` — the comparison `compare_values` applies inside
//!   this rank, NaN payloads included.
//! - **Strings**: rank byte, then the UTF-8 bytes with every 0x00 byte
//!   escaped to `0x00 0x01`, terminated by `0x00 0x00`. The escape keeps a
//!   payload from forging the terminator, so a string inside a composite
//!   value cannot be confused with a longer string plus a following
//!   element; the terminator is below every escaped continuation, so plain
//!   byte order stays the engine's `str` order.
//! - **Arrays**: rank byte, then each element's full encoding in element
//!   order. Element encodings are rank-tagged and strings are terminated,
//!   so the concatenation orders exactly as `compare_arrays` does: first
//!   differing element decides, then length (the shorter encoding is the
//!   prefix, and a prefix sorts first).
//! - **Objects**: rank byte, then for each field in `BTreeMap` (sorted
//!   name) order the escaped-and-terminated field name followed by the
//!   field value's full encoding — exactly `compare_objects`' name-then-
//!   value walk, with the fewer-fields side running out first (prefix,
//!   sorts first).
//!
//! ## Engine-order gaps (deliberate, none within `compare_values`)
//!
//! `encode_value` is exact for the engine's **order-by** order: the
//! pairwise test proves `encode(a) cmp encode(b) == compare_values(a, b)`
//! over a fixture set with every tricky pair. The gaps are in the
//! **predicate** layer, whose comparison is *not* `compare_values` (see
//! `super::model::compare` and `matches_condition`):
//!
//! - **Predicate equality is structural** (`DocumentValue`'s derived
//!   `PartialEq`): the engine's `Equals` rejects `Integer(5)` against
//!   `Number(5.0)`, while this encoding unifies them (as the order does).
//!   SQL equality on the key therefore over-matches exactly the
//!   cross-encoding-equal numeric class (`Integer(n)` vs `Number(n.0)`,
//!   and −0.0/0.0/0). Post-filter in Rust when that class must be exact.
//! - **Range predicates are same-domain and NaN-unordered** in the engine
//!   (`compare` returns `None` for cross-type and NaN operands, making the
//!   condition false), while the key order is the cross-type total order.
//!   A SQL range on the key therefore over-matches cross-domain values
//!   (e.g. strings above a numeric bound) and non-finite numbers. The
//!   caller post-filters, exactly as for `AllIn`/`EndsWith`/`Contains`.
//!
//! Nothing here is wired into any execution path: the storage seam adopts
//! it when that lane lands.

use crate::types::DocumentValue;

/// Rank byte of the null value (mirrors `super::model::rank`).
const RANK_NULL: u8 = 1;
/// Rank byte of the boolean values.
const RANK_BOOLEAN: u8 = 2;
/// Rank byte of the unified finite numeric domain (`i64` ∪ finite `f64`).
const RANK_NUMBER: u8 = 3;
/// Rank byte of the non-finite numbers (`±inf`, NaN).
const RANK_NON_FINITE: u8 = 4;
/// Rank byte of strings.
const RANK_STRING: u8 = 5;
/// Rank byte of arrays.
const RANK_ARRAY: u8 = 6;
/// Rank byte of objects.
const RANK_OBJECT: u8 = 7;

/// Sign sub-byte of positive finite numbers inside the rank-3 payload.
const SIGN_POSITIVE: u8 = 0x02;
/// The negative sign byte: the SMALLEST of the three sign bytes so every
/// negative orders before zero (0x01) and the positives (0x02) under
/// unsigned BINARY comparison — a complemented positive byte (0xFD) would
/// sort after them, which the pairwise order test catches.
const SIGN_NEGATIVE: u8 = 0x00;
/// The zero payload: sorts between the negative (0x00) and positive (0x02)
/// sign bytes.
const SIGN_ZERO: u8 = 0x01;
/// Bias added to the canonical exponent so it fits two unsigned bytes.
/// The smallest representable magnitude (a subnormal minimum `f64`) has
/// exponent −1074; the largest normal exponent is 1023.
const EXPONENT_BIAS: i32 = 1074;

/// Encodes one present document value as an order-preserving key.
///
/// Missing is not a value: an absent field is an absent key (SQL `NULL`),
/// and callers must never synthesize a byte string for it.
#[must_use]
pub(crate) fn encode_value(value: &DocumentValue) -> Vec<u8> {
    let mut out = Vec::new();
    push_value(&mut out, value);
    out
}

/// Encodes one query literal with the same order-preserving scheme.
///
/// The typed spec has no separate literal type: `QueryCondition` carries
/// its literals as [`DocumentValue`] (`super::spec`), so this is the same
/// encoding through the seam the SQL translator uses for bounds.
#[must_use]
pub(crate) fn encode_literal(value: &DocumentValue) -> Vec<u8> {
    encode_value(value)
}

/// Encodes the open lower bound of a string-prefix range: the string rank
/// byte plus the escaped prefix bytes, **without** the terminator, so every
/// encoding of a string starting with the prefix sorts at or above it.
///
/// Used by `StartsWith` range scans; the upper bound is the byte-string
/// successor of this value (see `super::sql_where`).
#[must_use]
pub(crate) fn encode_string_prefix(prefix: &str) -> Vec<u8> {
    let mut out = vec![RANK_STRING];
    push_escaped(&mut out, prefix.as_bytes());
    out
}

/// Pushes one value's full encoding (rank byte plus payload).
fn push_value(out: &mut Vec<u8>, value: &DocumentValue) {
    match value {
        DocumentValue::Null => out.push(RANK_NULL),
        DocumentValue::Boolean(boolean) => {
            out.push(RANK_BOOLEAN);
            out.push(u8::from(*boolean));
        }
        DocumentValue::Integer(integer) => {
            out.push(RANK_NUMBER);
            push_rational(out, *integer < 0, integer.unsigned_abs(), 0);
        }
        DocumentValue::Number(number) if number.is_finite() => {
            out.push(RANK_NUMBER);
            let bits = number.to_bits();
            let raw_exponent = ((bits >> 52) & 0x7ff) as i32;
            let fraction = bits & ((1_u64 << 52) - 1);
            let (mantissa, exponent) = if raw_exponent == 0 {
                (fraction, -1074)
            } else {
                ((1_u64 << 52) | fraction, raw_exponent - 1023 - 52)
            };
            push_rational(out, number.is_sign_negative(), mantissa, exponent);
        }
        DocumentValue::Number(number) => {
            out.push(RANK_NON_FINITE);
            let mut bytes = number.to_bits().to_be_bytes();
            if bytes[0] & 0x80 != 0 {
                // Negative patterns (NaN included) complement wholesale;
                // positive patterns only flip the sign bit. The result is
                // ascending `f64::total_cmp` order.
                for byte in &mut bytes {
                    *byte = !*byte;
                }
            } else {
                bytes[0] ^= 0x80;
            }
            out.extend_from_slice(&bytes);
        }
        DocumentValue::String(text) => {
            out.push(RANK_STRING);
            push_escaped(out, text.as_bytes());
            push_terminator(out);
        }
        DocumentValue::Array(elements) => {
            out.push(RANK_ARRAY);
            for element in elements {
                push_value(out, element);
            }
        }
        DocumentValue::Object(fields) => {
            out.push(RANK_OBJECT);
            for (name, field_value) in fields {
                // BTreeMap iteration is sorted-name order, the order
                // `compare_objects` walks.
                push_escaped(out, name.as_bytes());
                push_terminator(out);
                push_value(out, field_value);
            }
        }
    }
}

/// Pushes the encoding of `mantissa · 2^exponent` inside the rank-3
/// payload, for a possibly non-canonical nonzero pair (see the module doc
/// for the ordering argument). The pair is normalized to an odd mantissa
/// first, which is what makes `Integer(n)` and `Number(n as f64)` encode
/// identically.
fn push_rational(out: &mut Vec<u8>, negative: bool, mantissa: u64, exponent: i32) {
    if mantissa == 0 {
        out.push(SIGN_ZERO);
        return;
    }
    let trailing = mantissa.trailing_zeros().cast_signed();
    let mantissa = mantissa >> trailing;
    let exponent = exponent + trailing;
    let mut body = Vec::with_capacity(3 + 64);
    // Order first by the leading bit's exponent `E` (the value is
    // 2^E · 1.b1b2…bk), so every larger `E` is a larger value; comparing
    // the odd-mantissa exponent instead would overlap the ranges of
    // adjacent `E` (1 and 1.5 both normalize to odd mantissas of
    // different bit lengths) and misorder them.
    let lead = mantissa.ilog2().cast_signed();
    // The biased exponent is bounded by construction (IEEE-754 range plus
    // the fixed 1074 bias), so the narrowing is total.
    #[allow(
        clippy::cast_possible_truncation,
        clippy::cast_sign_loss,
        reason = "biased exponent bounded by f64 range"
    )]
    let biased = (exponent + lead + EXPONENT_BIAS) as u16;
    body.extend_from_slice(&biased.to_be_bytes());
    // One byte per mantissa bit below the leading one, most significant
    // first; the trailing 0x00 terminates the expansion.
    for index in (0..lead).rev() {
        body.push(u8::from((mantissa >> index) & 1 == 1));
    }
    body.push(0x00);
    out.push(if negative { SIGN_NEGATIVE } else { SIGN_POSITIVE });
    if negative {
        // Byte-complement the body so larger magnitudes sort earlier,
        // exactly reversing the positive order.
        out.extend(body.iter().map(|byte| !byte));
    } else {
        out.append(&mut body);
    }
}

/// Pushes the escaped UTF-8 bytes of one string component (value or object
/// field name): every 0x00 byte becomes `0x00 0x01`, so no payload can
/// contain the `0x00 0x00` terminator.
fn push_escaped(out: &mut Vec<u8>, bytes: &[u8]) {
    for byte in bytes {
        if *byte == 0x00 {
            out.extend_from_slice(&[0x00, 0x01]);
        } else {
            out.push(*byte);
        }
    }
}

/// Pushes the string-component terminator.
fn push_terminator(out: &mut Vec<u8>) {
    out.extend_from_slice(&[0x00, 0x00]);
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::query::model::compare_values;
    use std::cmp::Ordering;
    use std::collections::BTreeMap;

    /// The engine's order is the spec: for every fixture pair, the encoded
    /// keys must compare exactly as `compare_values` compares the values —
    /// `Equal` included, since equal-order values must encode identically
    /// for one key column to serve equality and order at once.
    #[test]
    fn encoding_matches_engine_order_for_every_fixture_pair() {
        let fixtures = fixtures();
        for (index, left) in fixtures.iter().enumerate() {
            let left_key = encode_value(left);
            for right in &fixtures[index + 1..] {
                let right_key = encode_value(right);
                let expected = compare_values(Some(left), Some(right));
                let actual = left_key.cmp(&right_key);
                assert_eq!(
                    actual, expected,
                    "pair {left:?} vs {right:?}: encoded order {actual:?}, engine order {expected:?}, keys {left_key:02x?} vs {right_key:02x?}"
                );
            }
        }
    }

    /// Sorting the fixtures by the engine's order must yield strictly
    /// increasing keys, one per distinct order class.
    #[test]
    fn engine_sorted_fixtures_have_strictly_increasing_keys() {
        let mut fixtures = fixtures();
        fixtures.sort_by(|left, right| compare_values(Some(left), Some(right)));
        fixtures.dedup_by(|left, right| compare_values(Some(left), Some(right)) == Ordering::Equal);
        let keys: Vec<Vec<u8>> = fixtures.iter().map(encode_value).collect();
        for pair in keys.windows(2) {
            assert!(
                pair[0] < pair[1],
                "keys must strictly increase after engine-order sorting: {:02x?} vs {:02x?}",
                pair[0],
                pair[1]
            );
        }
    }

    /// The unified numeric bucket interleaves `i64` and finite `f64`
    /// exactly, including the 2^53 spacing gap where integers fall between
    /// adjacent floats, and the extremes of both encodings.
    #[test]
    fn integers_and_floats_interleave_exactly() {
        let two_pow_53 = 9_007_199_254_740_992_i64;
        let values = [
            DocumentValue::Integer(i64::MIN),
            DocumentValue::Number(-f64::MAX),
            DocumentValue::Number(-9_007_199_254_740_993.0), // -(2^53 + 1)
            DocumentValue::Integer(-two_pow_53 - 1),
            DocumentValue::Number(-9_007_199_254_740_992.0),
            DocumentValue::Integer(-two_pow_53),
            DocumentValue::Integer(-1),
            DocumentValue::Number(-1.5),
            DocumentValue::Integer(0),
            DocumentValue::Number(-0.0),
            DocumentValue::Number(0.0),
            DocumentValue::Number(f64::MIN_POSITIVE),
            DocumentValue::Number(5e-324), // smallest subnormal
            DocumentValue::Number(0.5),
            DocumentValue::Integer(two_pow_53),
            DocumentValue::Number(9_007_199_254_740_992.0),
            DocumentValue::Integer(two_pow_53 + 1),
            DocumentValue::Number(9_007_199_254_740_994.0), // 2^53 + 2
            DocumentValue::Integer(i64::MAX),
            DocumentValue::Number(f64::MAX),
        ];
        for (index, left) in values.iter().enumerate() {
            for right in &values[index + 1..] {
                assert_eq!(
                    encode_value(left).cmp(&encode_value(right)),
                    compare_values(Some(left), Some(right)),
                    "pair {left:?} vs {right:?}"
                );
            }
        }
    }

    /// Equal-order values encode to identical keys: −0.0, 0.0, and
    /// `Integer(0)` are one order class, and `Integer(n)` equals
    /// `Number(n.0)` in the engine's order.
    #[test]
    fn equal_order_values_encode_identically() {
        let zeros =
            [DocumentValue::Number(-0.0), DocumentValue::Number(0.0), DocumentValue::Integer(0)];
        for (index, left) in zeros.iter().enumerate() {
            for right in &zeros[index + 1..] {
                assert_eq!(compare_values(Some(left), Some(right)), Ordering::Equal);
                assert_eq!(encode_value(left), encode_value(right));
            }
        }
        let five = DocumentValue::Integer(5);
        let five_float = DocumentValue::Number(5.0);
        assert_eq!(compare_values(Some(&five), Some(&five_float)), Ordering::Equal);
        assert_eq!(encode_value(&five), encode_value(&five_float));
    }

    /// Non-finite numbers follow `total_cmp` inside rank 4: −NaN payloads
    /// descending, −inf, +inf, +NaN payloads ascending — and NaN equals the
    /// same NaN.
    #[test]
    fn non_finite_numbers_follow_total_cmp() {
        let nan = f64::from_bits(0x7ff8_0000_0000_0000);
        let other_nan = f64::from_bits(0x7ff8_0000_0000_0001);
        let negative_nan = f64::from_bits(0xfff8_0000_0000_0000);
        let values = [
            DocumentValue::Number(negative_nan),
            DocumentValue::Number(f64::NEG_INFINITY),
            DocumentValue::Number(f64::INFINITY),
            DocumentValue::Number(nan),
            DocumentValue::Number(other_nan),
        ];
        for (index, left) in values.iter().enumerate() {
            for right in &values[index + 1..] {
                assert_eq!(
                    encode_value(left).cmp(&encode_value(right)),
                    compare_values(Some(left), Some(right)),
                    "pair {left:?} vs {right:?}"
                );
            }
        }
        assert_eq!(
            encode_value(&DocumentValue::Number(nan)),
            encode_value(&DocumentValue::Number(nan))
        );
    }

    /// The string-prefix lower bound sorts at or below the full encoding of
    /// every string carrying that prefix.
    #[test]
    fn string_prefix_bound_prefixes_every_matching_string() {
        for prefix in ["a", "a\u{0}", "hello", ""] {
            let bound = encode_string_prefix(prefix);
            for suffix in ["", "b", "\u{0}", "zzzz"] {
                let text = format!("{prefix}{suffix}");
                let key = encode_value(&DocumentValue::String(text.clone()));
                assert!(
                    bound <= key,
                    "prefix bound {bound:02x?} must not exceed key of {text:?} ({key:02x?})"
                );
            }
        }
    }

    /// Every value shape the encoder must order, including the tricky
    /// pairs: zero forms, NaN and infinities, NUL-carrying strings, prefix
    /// pairs, and nested composites.
    fn fixtures() -> Vec<DocumentValue> {
        let object = |pairs: &[(&str, DocumentValue)]| {
            DocumentValue::Object(
                pairs
                    .iter()
                    .map(|(name, value)| ((*name).to_string(), value.clone()))
                    .collect::<BTreeMap<_, _>>(),
            )
        };
        let array = |elements: &[DocumentValue]| DocumentValue::Array(elements.to_vec());
        vec![
            DocumentValue::Null,
            DocumentValue::Boolean(false),
            DocumentValue::Boolean(true),
            DocumentValue::Integer(i64::MIN),
            DocumentValue::Integer(-9_007_199_254_740_993), // -(2^53 + 1)
            DocumentValue::Integer(-1),
            DocumentValue::Integer(0),
            DocumentValue::Integer(1),
            DocumentValue::Integer(9_007_199_254_740_992), // 2^53
            DocumentValue::Integer(9_007_199_254_740_993), // 2^53 + 1
            DocumentValue::Integer(i64::MAX),
            DocumentValue::Number(f64::NAN),
            DocumentValue::Number(f64::NEG_INFINITY),
            DocumentValue::Number(-f64::MAX),
            DocumentValue::Number(-1.5),
            DocumentValue::Number(-0.0),
            DocumentValue::Number(0.0),
            DocumentValue::Number(5e-324),
            DocumentValue::Number(0.5),
            DocumentValue::Number(1.0),
            DocumentValue::Number(2.0),
            DocumentValue::Number(f64::INFINITY),
            DocumentValue::String(String::new()),
            DocumentValue::String("a".into()),
            DocumentValue::String("a\u{0}".into()),
            DocumentValue::String("a\u{0}\u{0}".into()),
            DocumentValue::String("a\u{5}b".into()),
            DocumentValue::String("ab".into()),
            DocumentValue::String("abc".into()),
            DocumentValue::String("b".into()),
            DocumentValue::String("b\u{fffd}".into()),
            DocumentValue::String("b\u{fffd}a".into()),
            DocumentValue::String("z".repeat(300)),
            DocumentValue::String("z".repeat(301)),
            DocumentValue::String("\u{10ffff}".into()),
            array(&[]),
            array(&[DocumentValue::Null]),
            array(&[DocumentValue::Integer(0)]),
            array(&[DocumentValue::Integer(0), DocumentValue::Integer(0)]),
            array(&[DocumentValue::Integer(1)]),
            array(&[DocumentValue::String("a".into())]),
            array(&[DocumentValue::String("a".into()), DocumentValue::String("b".into())]),
            array(&[DocumentValue::String("a\u{0}".into())]),
            array(&[DocumentValue::String("a\u{0}\u{0}".into())]),
            array(&[DocumentValue::String("ab".into())]),
            array(&[DocumentValue::Integer(1), DocumentValue::String("a".into())]),
            array(&[array(&[DocumentValue::Integer(1)])]),
            array(&[object(&[("a", DocumentValue::Integer(1))])]),
            object(&[]),
            object(&[("a", DocumentValue::Integer(1))]),
            object(&[("a", DocumentValue::Integer(2))]),
            object(&[("a", DocumentValue::Null)]),
            object(&[("a", DocumentValue::Integer(1)), ("b", DocumentValue::Integer(1))]),
            object(&[("a\u{0}", DocumentValue::Integer(1))]),
            object(&[("ab", DocumentValue::Integer(1))]),
            object(&[("a", array(&[DocumentValue::Integer(1)]))]),
            object(&[("a", object(&[("b", DocumentValue::Integer(1))]))]),
        ]
    }
}
