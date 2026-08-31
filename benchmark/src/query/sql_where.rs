//! `QueryCondition` → SQL translation for the `SQLite` auto-index side
//! tables (TB-23 groundwork, decisions D-30/D-31 in `GOAL.md`).
//!
//! The side table is `x_<index>(collection, id, <field>_key BLOB,
//! PRIMARY KEY(collection, id))` with a btree index on
//! `(collection, <field>_key, id)`, and `<field>_key` carries the
//! order-preserving encoding of [`super::key_encoding`]. Under BINARY
//! collation one column then serves equality, ranges, and `ORDER BY`:
//!
//! - an **absent field is SQL `NULL`** (never an encoded byte string), so
//!   `Exists`/`Missing` are `IS NOT NULL`/`IS NULL`, and `NotEquals` —
//!   which the engine satisfies for absent fields too — is
//!   `IS NULL OR <> ?`;
//! - a **present null value** is its rank-1 encoding blob, not SQL `NULL`,
//!   so it participates in comparisons exactly where the engine's order
//!   puts it;
//! - conditions are ANDed as a conjunction per `spec.rs`, with
//!   `?1..?n` placeholders bound in emission order to encoded-key blobs.
//!
//! ## Input contract
//!
//! The caller passes the conditions of **one field** — the field the side
//! table indexes — plus that table's key column name (a trusted
//! identifier: empty, quote-carrying, or NUL-carrying names refuse).
//! Conditions naming any other field refuse, because one key column
//! cannot serve two fields' predicates.
//!
//! ## Exactness and the post-filter set
//!
//! `Equals`, `NotEquals`, `Exists`, `Missing`, the four ranges, `Between`,
//! `In`, and `StartsWith` lower to SQL. Three classes are **not exact**
//! and the caller must know:
//!
//! - [`QueryCondition::AllIn`], [`QueryCondition::EndsWith`], and
//!   [`QueryCondition::Contains`] refuse with `Err(())`: no single-key
//!   shape serves them; the caller evaluates them in Rust over the SQL
//!   candidate set (an empty `AllIn` matches every document, so the
//!   caller drops it rather than refusing the query).
//! - `In` and `StartsWith` on **array fields** have element semantics in
//!   the engine (any string element of the array matches); the key
//!   compares the whole array value, so these lowerings are exact only
//!   for scalar string/number fields. Array-field queries post-filter.
//! - Range lowerings over-match the cross-domain and non-finite classes,
//!   and equality over-matches the cross-encoding-equal numerics, exactly
//!   as documented in [`super::key_encoding`] — the engine's predicate
//!   comparison is same-domain and structural, the key order is the
//!   total document order.
//!
//! Nothing here is wired into any execution path: the storage seam adopts
//! it when that lane lands.

use super::key_encoding::{encode_literal, encode_string_prefix};
use super::spec::{
    MAX_CONDITIONS, MAX_IN_VALUES, MAX_ORDERING_KEYS, NullsPlacement, OrderDirection, OrderingKey,
    QueryCondition,
};
use crate::types::DocumentValue;

/// One parameterized SQL fragment: the SQL text with `?1..?n`
/// placeholders and the encoded-key blobs bound in placeholder order.
pub(crate) struct SqlFragment {
    /// The SQL text (a bare predicate conjunction for
    /// [`condition_to_sql`], an `ORDER BY` clause for [`order_to_sql`]).
    pub(crate) sql: String,
    /// The bound encoded-key blobs, one per placeholder, in order.
    pub(crate) params: Vec<Vec<u8>>,
}

/// Lowers one field's predicate conjunction onto the side table's key
/// column.
///
/// # Errors
///
/// Returns `Err(())` for an empty or over-long conjunction
/// (> [`MAX_CONDITIONS`]), a `NotEquals`-style trusted-identifier problem
/// (empty, `"`, or NUL in `field_column`), conditions naming more than
/// one field, an over-long `In` list (> [`MAX_IN_VALUES`]) or an empty
/// `StartsWith` pattern (both mirror `QueryPredicate::all`'s refusals),
/// and the unsupported `AllIn`/`EndsWith`/`Contains` conditions the
/// caller must evaluate in Rust.
pub(crate) fn condition_to_sql(
    conditions: &[QueryCondition],
    field_column: &str,
) -> Result<SqlFragment, ()> {
    if conditions.is_empty() || conditions.len() > MAX_CONDITIONS {
        return Err(());
    }
    let column = quoted_identifier(field_column)?;
    let field = conditions[0].field();
    let mut params = Vec::new();
    let mut fragments = Vec::with_capacity(conditions.len());
    for condition in conditions {
        if condition.field() != field {
            return Err(());
        }
        let fragment = match condition {
            QueryCondition::Equals { value, .. } => {
                format!("{column} = {}", placeholder(&mut params, encode_literal(value)))
            }
            QueryCondition::NotEquals { value, .. } => {
                // The engine's NotEquals also matches the absent field.
                format!(
                    "{column} IS NULL OR {column} <> {}",
                    placeholder(&mut params, encode_literal(value))
                )
            }
            QueryCondition::GreaterThan { value, .. } => {
                format!("{column} > {}", placeholder(&mut params, encode_literal(value)))
            }
            QueryCondition::GreaterOrEqual { value, .. } => {
                format!("{column} >= {}", placeholder(&mut params, encode_literal(value)))
            }
            QueryCondition::LessThan { value, .. } => {
                format!("{column} < {}", placeholder(&mut params, encode_literal(value)))
            }
            QueryCondition::LessOrEqual { value, .. } => {
                format!("{column} <= {}", placeholder(&mut params, encode_literal(value)))
            }
            QueryCondition::Exists { .. } => format!("{column} IS NOT NULL"),
            QueryCondition::Missing { .. } => format!("{column} IS NULL"),
            QueryCondition::Between { low, high, .. } => {
                // V1's null-bound rule (model.rs `matches_between`): a
                // null bound is unbounded on its side, both-unbounded
                // matches every present value, and an unbounded upper
                // side additionally admits a present null.
                match (matches!(low, DocumentValue::Null), matches!(high, DocumentValue::Null)) {
                    (true, true) => format!("{column} IS NOT NULL"),
                    (true, false) => {
                        format!("{column} <= {}", placeholder(&mut params, encode_literal(high)))
                    }
                    (false, true) => {
                        let null_key =
                            placeholder(&mut params, encode_literal(&DocumentValue::Null));
                        let lower = placeholder(&mut params, encode_literal(low));
                        format!("({column} = {null_key} OR {column} >= {lower})")
                    }
                    (false, false) => {
                        let lower = placeholder(&mut params, encode_literal(low));
                        let upper = placeholder(&mut params, encode_literal(high));
                        format!("{column} >= {lower} AND {column} <= {upper}")
                    }
                }
            }
            QueryCondition::In { values, .. } => {
                if values.len() > MAX_IN_VALUES {
                    return Err(());
                }
                if values.is_empty() {
                    // Any-of-nothing matches nothing, absence included.
                    "0".to_string()
                } else {
                    let placeholders: Vec<String> = values
                        .iter()
                        .map(|value| placeholder(&mut params, encode_literal(value)))
                        .collect();
                    format!("{column} IN ({})", placeholders.join(", "))
                }
            }
            QueryCondition::StartsWith { value, .. } => {
                if value.is_empty() {
                    return Err(());
                }
                let lower_key = encode_string_prefix(value);
                let upper_key = successor(&lower_key);
                let lower = placeholder(&mut params, lower_key);
                match upper_key {
                    Some(upper_key) => {
                        let upper = placeholder(&mut params, upper_key);
                        format!("{column} >= {lower} AND {column} < {upper}")
                    }
                    // An all-0xFF prefix has no successor: every key at or
                    // above the lower bound matches (string rank bytes
                    // keep this unreachable in practice, but the bound is
                    // correct regardless).
                    None => format!("{column} >= {lower}"),
                }
            }
            // Element and substring semantics: no single-key lowering.
            QueryCondition::AllIn { .. }
            | QueryCondition::EndsWith { .. }
            | QueryCondition::Contains { .. } => return Err(()),
        };
        fragments.push(format!("({fragment})"));
    }
    Ok(SqlFragment { sql: fragments.join(" AND "), params })
}

/// Maps one ordering onto the side table's key column.
///
/// An empty ordering lowers to an empty fragment (no `ORDER BY` clause).
/// The engine groups missing and explicit null together; on the key
/// column SQL `NULL` is the missing field and the explicit-null value is
/// its rank-1 blob at the head of the value blobs, so:
///
/// - **Implicit** placement (no `NULLS` clause) is exact in both
///   directions — `SQLite` treats `NULL` as smallest, which is the engine's
///   missing-first order, and `DESC` reverses it wholesale exactly as the
///   engine's direction application does.
/// - **Explicit** `NULLS FIRST`/`NULLS LAST` moves the missing group
///   exactly; the explicit-null blob keeps its blob position at the head
///   of the values. A caller needing the whole missing-and-null group on
///   the explicit side stores explicit-null fields as SQL `NULL`
///   (adjusting `Exists`) or post-sorts.
///
/// # Errors
///
/// Returns `Err(())` for an identifier problem on `key_column`, an
/// ordering longer than [`MAX_ORDERING_KEYS`], or more than one ordering
/// key: one side table carries one key column, so a multi-key ordering
/// cannot be served by it and the caller falls back or post-sorts.
pub(crate) fn order_to_sql(order: &[OrderingKey], key_column: &str) -> Result<SqlFragment, ()> {
    let column = quoted_identifier(key_column)?;
    if order.len() > MAX_ORDERING_KEYS || order.len() > 1 {
        return Err(());
    }
    let Some(key) = order.first() else {
        return Ok(SqlFragment { sql: String::new(), params: Vec::new() });
    };
    let direction = match key.direction() {
        OrderDirection::Ascending => "ASC",
        OrderDirection::Descending => "DESC",
    };
    let nulls = match key.nulls_placement() {
        NullsPlacement::Implicit => "",
        NullsPlacement::First => " NULLS FIRST",
        NullsPlacement::Last => " NULLS LAST",
    };
    Ok(SqlFragment { sql: format!("ORDER BY {column} {direction}{nulls}"), params: Vec::new() })
}

/// Binds one encoded key and returns its placeholder.
fn placeholder(params: &mut Vec<Vec<u8>>, key: Vec<u8>) -> String {
    params.push(key);
    format!("?{}", params.len())
}

/// Quotes one trusted column identifier, refusing names that could escape
/// the quoting.
fn quoted_identifier(column: &str) -> Result<String, ()> {
    if column.is_empty() || column.contains('"') || column.contains('\0') {
        return Err(());
    }
    Ok(format!("\"{column}\""))
}

/// Returns the smallest byte string strictly greater than every string
/// carrying `bytes` as a prefix: the prefix up to and including the last
/// non-0xFF byte, that byte incremented. An all-0xFF string has no
/// successor (`None`): every byte string lies at or above it.
#[must_use]
fn successor(bytes: &[u8]) -> Option<Vec<u8>> {
    let position = bytes.iter().rposition(|byte| *byte != 0xff)?;
    let mut next = bytes[..=position].to_vec();
    next[position] += 1;
    Some(next)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::query::key_encoding::encode_value;

    fn equals(value: DocumentValue) -> QueryCondition {
        QueryCondition::Equals { field: "age".into(), value }
    }

    #[test]
    fn equals_lowers_to_key_equality() {
        let fragment =
            condition_to_sql(&[equals(DocumentValue::Integer(7))], "age_key").expect("lowers");
        assert_eq!(fragment.sql, "(\"age_key\" = ?1)");
        assert_eq!(fragment.params, vec![encode_value(&DocumentValue::Integer(7))]);
    }

    #[test]
    fn conjunctions_and_parameters_number_sequentially() {
        let conditions = vec![
            equals(DocumentValue::Integer(7)),
            QueryCondition::LessThan { field: "age".into(), value: DocumentValue::Integer(10) },
        ];
        let fragment = condition_to_sql(&conditions, "age_key").expect("lowers");
        assert_eq!(fragment.sql, "(\"age_key\" = ?1) AND (\"age_key\" < ?2)");
        assert_eq!(fragment.params.len(), 2);
    }

    #[test]
    fn not_equals_admits_the_absent_field() {
        let fragment = condition_to_sql(
            &[QueryCondition::NotEquals { field: "age".into(), value: DocumentValue::Integer(7) }],
            "age_key",
        )
        .expect("lowers");
        assert_eq!(fragment.sql, "(\"age_key\" IS NULL OR \"age_key\" <> ?1)");
    }

    #[test]
    fn ranges_lower_to_comparisons() {
        let shapes = [
            (
                QueryCondition::GreaterThan {
                    field: "age".into(),
                    value: DocumentValue::Integer(1),
                },
                "(\"age_key\" > ?1)",
            ),
            (
                QueryCondition::GreaterOrEqual {
                    field: "age".into(),
                    value: DocumentValue::Integer(1),
                },
                "(\"age_key\" >= ?1)",
            ),
            (
                QueryCondition::LessThan { field: "age".into(), value: DocumentValue::Integer(1) },
                "(\"age_key\" < ?1)",
            ),
            (
                QueryCondition::LessOrEqual {
                    field: "age".into(),
                    value: DocumentValue::Integer(1),
                },
                "(\"age_key\" <= ?1)",
            ),
        ];
        for (condition, expected) in shapes {
            let fragment = condition_to_sql(&[condition], "age_key").expect("lowers");
            assert_eq!(fragment.sql, expected);
            assert_eq!(fragment.params.len(), 1);
        }
    }

    #[test]
    fn exists_and_missing_are_null_checks() {
        let exists = condition_to_sql(&[QueryCondition::Exists { field: "age".into() }], "age_key")
            .expect("lowers");
        assert_eq!(exists.sql, "(\"age_key\" IS NOT NULL)");
        assert!(exists.params.is_empty());
        let missing =
            condition_to_sql(&[QueryCondition::Missing { field: "age".into() }], "age_key")
                .expect("lowers");
        assert_eq!(missing.sql, "(\"age_key\" IS NULL)");
    }

    #[test]
    fn between_follows_the_v1_null_bound_rule() {
        let between = |low: DocumentValue, high: DocumentValue| QueryCondition::Between {
            field: "age".into(),
            low,
            high,
        };
        let both_null =
            condition_to_sql(&[between(DocumentValue::Null, DocumentValue::Null)], "age_key")
                .expect("lowers");
        assert_eq!(both_null.sql, "(\"age_key\" IS NOT NULL)");
        assert!(both_null.params.is_empty());

        let unbounded_low = condition_to_sql(
            &[between(DocumentValue::Null, DocumentValue::Integer(10))],
            "age_key",
        )
        .expect("lowers");
        assert_eq!(unbounded_low.sql, "(\"age_key\" <= ?1)");

        let unbounded_high =
            condition_to_sql(&[between(DocumentValue::Integer(2), DocumentValue::Null)], "age_key")
                .expect("lowers");
        assert_eq!(unbounded_high.sql, "((\"age_key\" = ?1 OR \"age_key\" >= ?2))");
        assert_eq!(unbounded_high.params[0], encode_value(&DocumentValue::Null));
        assert_eq!(unbounded_high.params[1], encode_value(&DocumentValue::Integer(2)));

        let bounded = condition_to_sql(
            &[between(DocumentValue::Integer(2), DocumentValue::Integer(10))],
            "age_key",
        )
        .expect("lowers");
        assert_eq!(bounded.sql, "(\"age_key\" >= ?1 AND \"age_key\" <= ?2)");
    }

    #[test]
    fn in_lowers_to_a_key_in_list() {
        let values: Vec<DocumentValue> =
            [1, 2, 3].iter().map(|age| DocumentValue::Integer(*age)).collect();
        let fragment =
            condition_to_sql(&[QueryCondition::In { field: "age".into(), values }], "age_key")
                .expect("lowers");
        assert_eq!(fragment.sql, "(\"age_key\" IN (?1, ?2, ?3))");
        assert_eq!(fragment.params.len(), 3);
    }

    #[test]
    fn empty_in_matches_nothing() {
        let fragment = condition_to_sql(
            &[QueryCondition::In { field: "age".into(), values: Vec::new() }],
            "age_key",
        )
        .expect("lowers");
        assert_eq!(fragment.sql, "(0)");
        assert!(fragment.params.is_empty());
    }

    #[test]
    fn starts_with_scans_the_prefix_range() {
        let fragment = condition_to_sql(
            &[QueryCondition::StartsWith { field: "name".into(), value: "a".into() }],
            "name_key",
        )
        .expect("lowers");
        assert_eq!(fragment.sql, "(\"name_key\" >= ?1 AND \"name_key\" < ?2)");
        // Rank byte 0x05 plus 'a', and its successor carrying into the
        // array rank byte.
        assert_eq!(fragment.params, vec![vec![0x05, b'a'], vec![0x06]]);
    }

    #[test]
    fn starts_with_escapes_the_pattern() {
        let fragment = condition_to_sql(
            &[QueryCondition::StartsWith { field: "name".into(), value: "a\u{0}".into() }],
            "name_key",
        )
        .expect("lowers");
        // The NUL escapes to 0x00 0x01, so the successor increments the
        // escape's second byte.
        assert_eq!(
            fragment.params,
            vec![vec![0x05, b'a', 0x00, 0x01], vec![0x05, b'a', 0x00, 0x02]]
        );
    }

    #[test]
    fn successor_carries_past_0xff_tails_and_reports_exhaustion() {
        assert_eq!(successor(&[0x05, b'a']), Some(vec![0x05, b'b']));
        assert_eq!(successor(&[0x01, 0xff, 0xff]), Some(vec![0x02]));
        assert_eq!(successor(&[0xff]), None);
        assert_eq!(successor(&[]), None);
    }

    #[test]
    fn unsupported_conditions_refuse() {
        for condition in [
            QueryCondition::AllIn { field: "age".into(), values: vec![DocumentValue::Integer(1)] },
            QueryCondition::EndsWith { field: "name".into(), value: "a".into() },
            QueryCondition::Contains { field: "name".into(), value: "a".into() },
        ] {
            assert!(condition_to_sql(&[condition], "age_key").is_err());
        }
    }

    #[test]
    fn bounds_violations_refuse() {
        assert!(condition_to_sql(&[], "age_key").is_err());

        let too_many: Vec<QueryCondition> =
            (0..=MAX_CONDITIONS).map(|_| equals(DocumentValue::Integer(1))).collect();
        assert!(condition_to_sql(&too_many, "age_key").is_err());

        let over_long_in: Vec<DocumentValue> = (0..=32_i64).map(DocumentValue::Integer).collect();
        assert!(
            condition_to_sql(
                &[QueryCondition::In { field: "age".into(), values: over_long_in }],
                "age_key"
            )
            .is_err()
        );

        assert!(
            condition_to_sql(
                &[QueryCondition::StartsWith { field: "name".into(), value: String::new() }],
                "name_key"
            )
            .is_err()
        );
    }

    #[test]
    fn mixed_fields_and_bad_identifiers_refuse() {
        let mixed = vec![
            equals(DocumentValue::Integer(7)),
            QueryCondition::Exists { field: "other".into() },
        ];
        assert!(condition_to_sql(&mixed, "age_key").is_err());
        for bad in ["", "age\"key", "age\0key"] {
            assert!(condition_to_sql(&[equals(DocumentValue::Integer(7))], bad).is_err());
            assert!(order_to_sql(&[], bad).is_err());
        }
    }

    #[test]
    fn order_maps_direction_and_nulls_placement() {
        assert_eq!(order_to_sql(&[], "age_key").expect("lowers").sql, "");

        let ascending = OrderingKey::try_new("age", OrderDirection::Ascending).expect("valid");
        assert_eq!(
            order_to_sql(&[ascending], "age_key").expect("lowers").sql,
            "ORDER BY \"age_key\" ASC"
        );

        let descending_first = OrderingKey::try_new_with_nulls(
            "age",
            OrderDirection::Descending,
            NullsPlacement::First,
        )
        .expect("valid");
        assert_eq!(
            order_to_sql(&[descending_first], "age_key").expect("lowers").sql,
            "ORDER BY \"age_key\" DESC NULLS FIRST"
        );

        let ascending_last =
            OrderingKey::try_new_with_nulls("age", OrderDirection::Ascending, NullsPlacement::Last)
                .expect("valid");
        assert_eq!(
            order_to_sql(&[ascending_last], "age_key").expect("lowers").sql,
            "ORDER BY \"age_key\" ASC NULLS LAST"
        );
    }

    #[test]
    fn multi_key_orderings_refuse() {
        let first = OrderingKey::try_new("age", OrderDirection::Ascending).expect("valid");
        let second = OrderingKey::try_new("name", OrderDirection::Ascending).expect("valid");
        assert!(order_to_sql(&[first, second], "age_key").is_err());

        let too_many: Vec<OrderingKey> = (0..=MAX_ORDERING_KEYS)
            .map(|index| {
                OrderingKey::try_new(&format!("f{index}"), OrderDirection::Ascending)
                    .expect("valid")
            })
            .collect();
        assert!(order_to_sql(&too_many, "age_key").is_err());
    }
}
