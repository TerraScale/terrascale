use crate::Record;
use serde::{Deserialize, Serialize};
use serde_json::Value;

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
/// Predicate connector, evaluated in request order.
#[derive(Clone, Copy, Debug, Default, Serialize, Deserialize)]
pub enum Connector {
    #[default]
    AND,
    OR,
}
/// String sensitivity.
#[derive(Clone, Copy, Debug, Default, Serialize, Deserialize)]
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

fn compare_numbers(left: &serde_json::Number, right: &serde_json::Number) -> std::cmp::Ordering {
    use std::cmp::Ordering;

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

fn one(record: &Record, predicate: &Predicate) -> bool {
    let value = scalar(record.get(&predicate.field).unwrap_or(&Value::Null), predicate.mode);
    let expected = scalar(&predicate.value, predicate.mode);
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
        Operator::In => predicate
            .value
            .as_array()
            .is_some_and(|values| values.iter().any(|item| scalar(item, predicate.mode) == value)),
        Operator::NotIn => predicate
            .value
            .as_array()
            .is_some_and(|values| values.iter().all(|item| scalar(item, predicate.mode) != value)),
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
    let mut result = None;
    for predicate in predicates {
        let matched = one(record, predicate);
        result = Some(match (result, predicate.connector) {
            (None, _) => matched,
            (Some(prior), Connector::AND) => prior && matched,
            (Some(prior), Connector::OR) => prior || matched,
        });
    }
    result.unwrap_or(true)
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
