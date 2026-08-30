//! JSON conversion for the engine's structured document value.
//!
//! The engine is deliberately serde-free, so the studio owns this bridge. It
//! is the only place a document's value crosses into text, and it keeps key
//! order deterministic because the engine's object is a `BTreeMap`.

use std::collections::BTreeMap;

use terrabase::DocumentValue;

/// Converts a parsed JSON value into the engine's structured value.
pub fn from_json(value: serde_json::Value) -> DocumentValue {
    match value {
        serde_json::Value::Null => DocumentValue::Null,
        serde_json::Value::Bool(flag) => DocumentValue::Boolean(flag),
        serde_json::Value::Number(number) => {
            if let Some(integer) = number.as_i64() {
                DocumentValue::Integer(integer)
            } else {
                DocumentValue::Number(number.as_f64().unwrap_or_default())
            }
        }
        serde_json::Value::String(text) => DocumentValue::String(text),
        serde_json::Value::Array(items) => {
            DocumentValue::Array(items.into_iter().map(from_json).collect())
        }
        serde_json::Value::Object(map) => DocumentValue::Object(
            map.into_iter().map(|(key, value)| (key, from_json(value))).collect::<BTreeMap<_, _>>(),
        ),
    }
}

/// Converts the engine's structured value into pretty, deterministic JSON.
///
/// Non-finite floats cannot appear in JSON; the engine never produces them
/// from parsed JSON, and the editor rejects them on the way in, so this
/// renders them as `null` rather than inventing a sentinel.
pub fn to_display(value: &DocumentValue) -> String {
    serde_json::to_string_pretty(&to_json(value)).unwrap_or_else(|_| "null".to_owned())
}

/// Converts the engine's structured value into a compact JSON string.
pub fn to_compact(value: &DocumentValue) -> String {
    serde_json::to_string(&to_json(value)).unwrap_or_else(|_| "null".to_owned())
}

/// Converts the engine's structured value into parsed JSON.
pub fn to_json(value: &DocumentValue) -> serde_json::Value {
    match value {
        DocumentValue::Null => serde_json::Value::Null,
        DocumentValue::Boolean(flag) => serde_json::Value::Bool(*flag),
        DocumentValue::Integer(integer) => serde_json::Value::Number((*integer).into()),
        DocumentValue::Number(number) => match serde_json::Number::from_f64(*number) {
            Some(number) => serde_json::Value::Number(number),
            None => serde_json::Value::Null,
        },
        DocumentValue::String(text) => serde_json::Value::String(text.clone()),
        DocumentValue::Array(items) => {
            serde_json::Value::Array(items.iter().map(to_json).collect())
        }
        DocumentValue::Object(map) => serde_json::Value::Object(
            map.iter().map(|(key, value)| (key.clone(), to_json(value))).collect(),
        ),
    }
}

/// The first `limit` top-level field names of a document, for list previews.
pub fn preview_fields(value: &DocumentValue, limit: usize) -> Vec<String> {
    let DocumentValue::Object(map) = value else {
        return Vec::new();
    };
    map.keys().take(limit).cloned().collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn json_round_trips_through_the_structured_value() {
        let parsed: serde_json::Value =
            serde_json::from_str("{\"b\": 1, \"a\": [true, null, 1.5], \"c\": \"x\"}").unwrap();
        let structured = from_json(parsed);
        assert_eq!(to_compact(&structured), "{\"a\":[true,null,1.5],\"b\":1,\"c\":\"x\"}");
    }

    #[test]
    fn integers_stay_integers() {
        let structured = from_json(serde_json::json!(7));
        assert_eq!(structured, DocumentValue::Integer(7));
        assert_eq!(to_compact(&structured), "7");
    }

    #[test]
    fn object_keys_render_in_sorted_order() {
        let structured = from_json(serde_json::json!({"z": 1, "a": 2}));
        assert_eq!(to_compact(&structured), "{\"a\":2,\"z\":1}");
    }

    #[test]
    fn preview_fields_lists_the_leading_keys_in_canonical_order() {
        let structured = from_json(serde_json::json!({"one": 1, "two": 2, "three": 3}));
        // The engine's object is a BTreeMap, so the preview follows canonical
        // (sorted) key order rather than insertion order.
        assert_eq!(preview_fields(&structured, 2), vec!["one", "three"]);
        assert_eq!(preview_fields(&structured, 10), vec!["one", "three", "two"]);
        assert_eq!(preview_fields(&DocumentValue::Integer(1), 3), Vec::<String>::new());
    }
}
