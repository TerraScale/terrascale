use crate::Record;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::cmp::Ordering;

/// Public Better Auth predicate. Unknown operator spellings are rejected by decoding.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Predicate {
    /// Exact field name.
    pub field: String,
    /// Typed scalar or array value.
    pub value: Value,
    /// Comparison operator; equality by default.
    #[serde(default)]
    pub operator: Operator,
    /// Logical connector; AND by default.
    #[serde(default)]
    pub connector: Connector,
    /// String matching mode.
    #[serde(default)]
    pub mode: Mode,
}
/// Deterministic scalar ordering for bounded Better Auth pages.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct QuerySort {
    pub field: String,
    #[serde(default)]
    pub direction: SortDirection,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct QueryJoin {
    pub model: String,
    pub output_field: String,
    pub from_field: String,
    pub to_field: String,
    pub relation: JoinRelation,
    pub limit: u32,
}

#[derive(Clone, Copy, Debug, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum JoinRelation {
    OneToOne,
    OneToMany,
}

#[derive(Clone, Copy, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum SortDirection {
    #[default]
    Asc,
    Desc,
}
/// Supported comparisons.
#[derive(Clone, Copy, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Operator {
    #[default]
    Eq,
    Ne,
    Lt,
    Lte,
    Gt,
    Gte,
    In,
    NotIn,
    Contains,
    StartsWith,
    EndsWith,
}
/// Predicate connector for the SQL-style AND group and OR group.
#[derive(Clone, Copy, Debug, Default, Serialize, Deserialize)]
pub enum Connector {
    #[default]
    AND,
    OR,
}
/// String sensitivity.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Mode {
    #[default]
    Sensitive,
    Insensitive,
}

fn scalar(value: &Value, mode: Mode) -> Value {
    if let (Value::String(value), Mode::Insensitive) = (value, mode) {
        Value::String(value.to_lowercase())
    } else {
        value.clone()
    }
}

fn compare_numbers(left: &serde_json::Number, right: &serde_json::Number) -> Ordering {
    let integer = |value: &serde_json::Number| {
        value.as_i64().map(i128::from).or_else(|| value.as_u64().map(i128::from))
    };
    let integer_float = |integer: i128, float: f64| {
        if float < i64::MIN as f64 {
            return Ordering::Greater;
        }
        // u64::MAX rounds up to exactly 2^64 as f64.
        if float >= u64::MAX as f64 {
            return Ordering::Less;
        }
        match integer.cmp(&(float as i128)) {
            Ordering::Equal => 0.0_f64.partial_cmp(&float.fract()).expect("finite JSON number"),
            order => order,
        }
    };
    match (integer(left), integer(right)) {
        (Some(left), Some(right)) => left.cmp(&right),
        (Some(left), None) => integer_float(left, right.as_f64().expect("JSON number")),
        (None, Some(right)) => integer_float(right, left.as_f64().expect("JSON number")).reverse(),
        (None, None) => left
            .as_f64()
            .expect("JSON number")
            .partial_cmp(&right.as_f64().expect("JSON number"))
            .expect("finite JSON numbers"),
    }
}

fn value_rank(value: &Value) -> u8 {
    match value {
        Value::Null => 0,
        Value::Bool(_) => 1,
        Value::Number(_) => 2,
        Value::String(_) => 3,
        Value::Array(_) => 4,
        Value::Object(_) => 5,
    }
}

pub(crate) fn compare_values(left: &Value, right: &Value) -> Ordering {
    let rank = value_rank(left).cmp(&value_rank(right));
    if rank != Ordering::Equal {
        return rank;
    }
    match (left, right) {
        (Value::Null, Value::Null) => Ordering::Equal,
        (Value::Bool(left), Value::Bool(right)) => left.cmp(right),
        (Value::Number(left), Value::Number(right)) => compare_numbers(left, right),
        (Value::String(left), Value::String(right)) => left.cmp(right),
        (Value::Array(left), Value::Array(right)) => encode_json(left).cmp(&encode_json(right)),
        (Value::Object(left), Value::Object(right)) => encode_json(left).cmp(&encode_json(right)),
        _ => Ordering::Equal,
    }
}

fn encode_json(value: &impl Serialize) -> String {
    serde_json::to_string(value).expect("JSON values are serializable")
}

pub(crate) fn compare_records(left: &Record, right: &Record, sort: &QuerySort) -> Ordering {
    let ordering = compare_values(
        left.get(&sort.field).unwrap_or(&Value::Null),
        right.get(&sort.field).unwrap_or(&Value::Null),
    );
    let ordering = match sort.direction {
        SortDirection::Asc => ordering,
        SortDirection::Desc => ordering.reverse(),
    };
    if ordering != Ordering::Equal {
        return ordering;
    }
    left.get("id")
        .and_then(Value::as_str)
        .unwrap_or("")
        .cmp(right.get("id").and_then(Value::as_str).unwrap_or(""))
}

fn one(record: &Record, predicate: &Predicate) -> bool {
    // Keep predicate behavior aligned with the maintained SQL adapters:
    // missing fields are SQL NULL, so equality and inequality have explicit
    // null semantics rather than ordinary JSON inequality.
    let raw_value = record.get(&predicate.field).unwrap_or(&Value::Null);
    let value = raw_value;
    let expected = &predicate.value;
    let value_is_null = value.is_null();
    let expected_is_null = expected.is_null();
    if matches!(predicate.operator, Operator::Eq | Operator::Ne)
        && (value_is_null || expected_is_null)
    {
        return match predicate.operator {
            Operator::Eq => value_is_null && expected_is_null,
            Operator::Ne => !value_is_null && expected_is_null,
            _ => unreachable!(),
        };
    }
    let value = scalar(value, predicate.mode);
    let expected = scalar(expected, predicate.mode);
    let ordering = match (&value, &expected) {
        (Value::Number(a), Value::Number(b)) => Some(compare_numbers(a, b)),
        (Value::String(a), Value::String(b)) => Some(a.cmp(b)),
        _ => None,
    };
    match predicate.operator {
        Operator::Eq => value == expected,
        Operator::Ne => value != expected,
        Operator::Lt => ordering.is_some_and(|order| order.is_lt()),
        Operator::Lte => ordering.is_some_and(|order| order.is_le()),
        Operator::Gt => ordering.is_some_and(|order| order.is_gt()),
        Operator::Gte => ordering.is_some_and(|order| order.is_ge()),
        Operator::In => predicate.value.as_array().is_some_and(|values| {
            !value_is_null && !values.is_empty() && {
                let insensitive =
                    predicate.mode == Mode::Insensitive && values.iter().all(Value::is_string);
                let comparison_value = if insensitive {
                    scalar(raw_value, Mode::Insensitive)
                } else {
                    raw_value.clone()
                };
                values.iter().any(|item| {
                    let item =
                        if insensitive { scalar(item, Mode::Insensitive) } else { item.clone() };
                    item == comparison_value
                })
            }
        }),
        Operator::NotIn => predicate.value.as_array().is_some_and(|values| {
            if values.is_empty() {
                return true;
            }
            if value_is_null || values.iter().any(Value::is_null) {
                return false;
            }
            let insensitive =
                predicate.mode == Mode::Insensitive && values.iter().all(Value::is_string);
            let comparison_value =
                if insensitive { scalar(raw_value, Mode::Insensitive) } else { raw_value.clone() };
            values.iter().all(|item| {
                let item = if insensitive { scalar(item, Mode::Insensitive) } else { item.clone() };
                item != comparison_value
            })
        }),
        Operator::Contains => value
            .as_str()
            .zip(expected.as_str())
            .is_some_and(|(value, expected)| value.contains(expected)),
        Operator::StartsWith => value
            .as_str()
            .zip(expected.as_str())
            .is_some_and(|(value, expected)| value.starts_with(expected)),
        Operator::EndsWith => value
            .as_str()
            .zip(expected.as_str())
            .is_some_and(|(value, expected)| value.ends_with(expected)),
    }
}
pub(crate) fn matches(record: &Record, predicates: &[Predicate]) -> bool {
    // SQL grouping is all AND terms, combined with the OR term group. This
    // prevents an OR email predicate from bypassing a tenant guard.
    let and_matches = predicates
        .iter()
        .filter(|predicate| matches!(predicate.connector, Connector::AND))
        .all(|predicate| one(record, predicate));
    let or_terms: Vec<_> = predicates
        .iter()
        .filter(|predicate| matches!(predicate.connector, Connector::OR))
        .collect();
    and_matches
        && (or_terms.is_empty() || or_terms.into_iter().any(|predicate| one(record, predicate)))
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn numeric_predicates_preserve_integer_precision_and_fractional_order() {
        for (lower, upper) in [
            (json!(9_007_199_254_740_992_u64), json!(9_007_199_254_740_993_u64)),
            (json!(u64::MAX - 1), json!(u64::MAX)),
            (json!(i64::MIN), json!(i64::MIN + 1)),
            (json!(-1), json!(u64::MAX)),
            (json!(9_007_199_254_740_992.0), json!(9_007_199_254_740_993_u64)),
            (json!(u64::MAX), json!(18_446_744_073_709_551_616.0)),
            (json!(-1.5), json!(-1)),
            (json!(1), json!(1.5)),
        ] {
            let predicate = |value, operator| Predicate {
                field: "counter".into(),
                value,
                operator,
                connector: Connector::AND,
                mode: Mode::Sensitive,
            };
            let record = Record::from([("counter".into(), lower.clone())]);
            assert!(
                matches(&record, &[predicate(upper.clone(), Operator::Lt)]),
                "{lower} < {upper}"
            );
            assert!(!matches(&record, &[predicate(upper.clone(), Operator::Gte)]));
            let record = Record::from([("counter".into(), upper)]);
            assert!(matches(&record, &[predicate(lower, Operator::Gt)]));
        }
    }
}
