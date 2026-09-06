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
fn one(record: &Record, predicate: &Predicate) -> bool {
    let value = scalar(record.get(&predicate.field).unwrap_or(&Value::Null), predicate.mode);
    let expected = scalar(&predicate.value, predicate.mode);
    let ordering = match (&value, &expected) {
        (Value::Number(a), Value::Number(b)) => {
            a.as_f64().zip(b.as_f64()).and_then(|(a, b)| a.partial_cmp(&b))
        }
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
