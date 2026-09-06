//! Bounded interchange codecs used by import and export jobs.
//!
//! The codecs operate on one record at a time and keep the document value as
//! JSON bytes.  This is important for imports: a malformed or oversized
//! record is rejected before it reaches storage, and a caller can persist the
//! byte offset after the corresponding storage write has committed.

use base64::Engine as _;
use serde_json::Value;
use std::fmt;

/// Supported import/export interchange formats.
#[derive(Clone, Copy, Debug, Eq, PartialEq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum TransferFormat {
    /// TerraBase native bundle members, including history and causal metadata.
    Native,
    /// One envelope per line.
    Jsonl,
    /// RFC 4180 CSV with a `value` JSON column.
    Csv,
    /// MongoDB extended JSON documents.
    MongoJson,
    /// Cosmos DB JSON documents.
    CosmosJson,
    /// DynamoDB JSON export envelopes.
    DynamoJson,
}

impl Default for TransferFormat {
    fn default() -> Self {
        Self::Native
    }
}

/// Stable artifact suffix for the selected encoding.
#[must_use]
pub const fn extension(format: TransferFormat) -> &'static str {
    match format {
        TransferFormat::Native => "tbx",
        TransferFormat::Jsonl => "jsonl",
        TransferFormat::Csv => "csv",
        TransferFormat::MongoJson | TransferFormat::CosmosJson => "json",
        TransferFormat::DynamoJson => "json",
    }
}

/// MIME type paired with [`extension`].
#[must_use]
pub const fn content_type(format: TransferFormat) -> &'static str {
    match format {
        TransferFormat::Native => "application/vnd.terrascale.export+jsonl",
        TransferFormat::Jsonl
        | TransferFormat::MongoJson
        | TransferFormat::CosmosJson
        | TransferFormat::DynamoJson => "application/jsonl",
        TransferFormat::Csv => "text/csv",
    }
}

/// One logical document accepted by an import writer.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ImportRecord {
    /// Collection name, if supplied by the source format.
    pub collection: String,
    /// Stable document identifier.
    pub id: String,
    /// Canonical JSON document body.
    pub value: Vec<u8>,
}

/// Codec failures are deliberately free of source contents.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CodecError {
    /// A record is malformed for the selected format.
    Malformed,
    /// A record exceeded the configured bound.
    Oversized,
    /// The input is not valid UTF-8 where the format requires it.
    Utf8,
}

impl fmt::Display for CodecError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            Self::Malformed => "malformed import record",
            Self::Oversized => "import record exceeds configured bound",
            Self::Utf8 => "import record is not UTF-8",
        })
    }
}

impl std::error::Error for CodecError {}

/// Decodes bounded records. `max_record` is checked before parsing each
/// record, so hostile input cannot make the parser allocate an unbounded value.
pub fn decode(
    input: &[u8],
    format: TransferFormat,
    max_record: usize,
) -> Result<Vec<ImportRecord>, CodecError> {
    if max_record == 0 {
        return Err(CodecError::Oversized);
    }
    match format {
        TransferFormat::Native | TransferFormat::Jsonl | TransferFormat::DynamoJson => input
            .split(|byte| *byte == b'\n')
            .filter(|line| !line.iter().all(u8::is_ascii_whitespace))
            .map(|line| decode_one(line, format, max_record))
            .collect(),
        TransferFormat::MongoJson | TransferFormat::CosmosJson => {
            if input.len() > max_record.saturating_mul(2_048) {
                return Err(CodecError::Oversized);
            }
            let values: Vec<Value> =
                serde_json::from_slice(input).map_err(|_| CodecError::Malformed)?;
            values.into_iter().map(|value| decode_value(value, format, max_record)).collect()
        }
        TransferFormat::Csv => decode_csv(input, max_record),
    }
}

/// Encodes records into the selected format. Values must already be canonical
/// JSON; they are validated without converting them into a storage object.
pub fn encode(records: &[ImportRecord], format: TransferFormat) -> Result<Vec<u8>, CodecError> {
    let mut out = Vec::new();
    if format == TransferFormat::Csv {
        out.extend_from_slice(b"collection,id,value\n");
    }
    for record in records {
        let value: Value =
            serde_json::from_slice(&record.value).map_err(|_| CodecError::Malformed)?;
        match format {
            TransferFormat::Native | TransferFormat::Jsonl => {
                let envelope = serde_json::json!({"collection": record.collection, "id": record.id, "value": value});
                serde_json::to_writer(&mut out, &envelope).map_err(|_| CodecError::Malformed)?;
                out.push(b'\n');
            }
            TransferFormat::Csv => {
                csv_field(&mut out, &record.collection);
                out.push(b',');
                csv_field(&mut out, &record.id);
                out.push(b',');
                let body = serde_json::to_vec(&value).map_err(|_| CodecError::Malformed)?;
                csv_field(&mut out, std::str::from_utf8(&body).map_err(|_| CodecError::Utf8)?);
                out.push(b'\n');
            }
            TransferFormat::MongoJson | TransferFormat::CosmosJson => {
                if !out.is_empty() {
                    out.push(b',');
                }
                let mut envelope = serde_json::Map::new();
                envelope.insert(
                    if format == TransferFormat::MongoJson { "_id".into() } else { "id".into() },
                    Value::String(record.id.clone()),
                );
                envelope.insert("value".into(), value);
                serde_json::to_writer(&mut out, &Value::Object(envelope))
                    .map_err(|_| CodecError::Malformed)?;
            }
            TransferFormat::DynamoJson => {
                let envelope = serde_json::json!({"Item":{"id":{"S":record.id},"value":{"M": dynamo_map(&value)?}}});
                serde_json::to_writer(&mut out, &envelope).map_err(|_| CodecError::Malformed)?;
                out.push(b'\n');
            }
        }
    }
    if matches!(format, TransferFormat::MongoJson | TransferFormat::CosmosJson) {
        out.splice(0..0, [b'[']);
        out.push(b']');
    }
    Ok(out)
}

/// Encodes current document records produced by the export source. History
/// remains in the dedicated history member and is intentionally not flattened.
pub fn encode_documents(
    records: &[crate::export_api::ExportRecord],
    format: TransferFormat,
) -> Result<Vec<u8>, CodecError> {
    let records = records
        .iter()
        .filter_map(|record| match record {
            crate::export_api::ExportRecord::Document {
                collection, id, canonical_json, ..
            } => Some(ImportRecord {
                collection: collection.clone(),
                id: id.clone(),
                value: canonical_json.clone(),
            }),
            crate::export_api::ExportRecord::History { .. } => None,
        })
        .collect::<Vec<_>>();
    encode(&records, format)
}

fn decode_one(line: &[u8], format: TransferFormat, max: usize) -> Result<ImportRecord, CodecError> {
    if line.len() > max {
        return Err(CodecError::Oversized);
    }
    let value: Value = serde_json::from_slice(line).map_err(|_| CodecError::Malformed)?;
    if format == TransferFormat::DynamoJson {
        let item = value.get("Item").ok_or(CodecError::Malformed)?;
        let id = item
            .get("id")
            .and_then(|v| v.get("S"))
            .and_then(Value::as_str)
            .ok_or(CodecError::Malformed)?;
        let body = item.get("value").and_then(|v| v.get("M")).ok_or(CodecError::Malformed)?;
        return Ok(ImportRecord {
            collection: String::new(),
            id: id.into(),
            value: serde_json::to_vec(&from_dynamo_map(body)?)
                .map_err(|_| CodecError::Malformed)?,
        });
    }
    decode_value(value, format, max)
}

fn decode_value(
    value: Value,
    format: TransferFormat,
    max: usize,
) -> Result<ImportRecord, CodecError> {
    let object = value.as_object().ok_or(CodecError::Malformed)?;
    let id_key = if format == TransferFormat::MongoJson { "_id" } else { "id" };
    let id = object.get(id_key).and_then(Value::as_str).ok_or(CodecError::Malformed)?.to_owned();
    let body = object.get("value").cloned().unwrap_or_else(|| {
        let mut body = object.clone();
        body.remove(id_key);
        Value::Object(body)
    });
    let value = serde_json::to_vec(&body).map_err(|_| CodecError::Malformed)?;
    if value.len() > max {
        return Err(CodecError::Oversized);
    }
    Ok(ImportRecord {
        collection: object.get("collection").and_then(Value::as_str).unwrap_or_default().into(),
        id,
        value,
    })
}

fn decode_csv(input: &[u8], max: usize) -> Result<Vec<ImportRecord>, CodecError> {
    let mut rows = csv_rows(input, max)?;
    let header = rows.next().ok_or(CodecError::Malformed)?;
    if header.len() != 3 || header[0] != "collection" || header[1] != "id" || header[2] != "value" {
        return Err(CodecError::Malformed);
    }
    rows.map(|row| {
        if row.len() != 3 {
            return Err(CodecError::Malformed);
        }
        let value = serde_json::from_str::<Value>(&row[2]).map_err(|_| CodecError::Malformed)?;
        let value = serde_json::to_vec(&value).map_err(|_| CodecError::Malformed)?;
        if value.len() > max {
            return Err(CodecError::Oversized);
        }
        Ok(ImportRecord { collection: row[0].clone(), id: row[1].clone(), value })
    })
    .collect()
}

fn csv_rows(
    input: &[u8],
    max: usize,
) -> Result<impl Iterator<Item = Vec<String>> + '_, CodecError> {
    if input.len() > max.saturating_mul(2_048) {
        return Err(CodecError::Oversized);
    }
    let text = std::str::from_utf8(input).map_err(|_| CodecError::Utf8)?;
    Ok(text.lines().map(|line| {
        let mut fields = Vec::new();
        let mut field = String::new();
        let mut quoted = false;
        let mut chars = line.chars().peekable();
        while let Some(ch) = chars.next() {
            match ch {
                '"' if quoted && chars.peek() == Some(&'"') => {
                    field.push('"');
                    chars.next();
                }
                '"' => quoted = !quoted,
                ',' if !quoted => {
                    fields.push(std::mem::take(&mut field));
                }
                _ => field.push(ch),
            }
        }
        fields.push(field);
        fields
    }))
}

fn csv_field(out: &mut Vec<u8>, field: &str) {
    out.push(b'"');
    for byte in field.bytes() {
        if byte == b'"' {
            out.push(b'"');
        }
        out.push(byte);
    }
    out.push(b'"');
}

fn dynamo_map(value: &Value) -> Result<serde_json::Map<String, Value>, CodecError> {
    let object = value.as_object().ok_or(CodecError::Malformed)?;
    object.iter().map(|(key, value)| Ok((key.clone(), dynamo_attribute(value)?))).collect()
}
fn dynamo_attribute(value: &Value) -> Result<Value, CodecError> {
    Ok(if let Some(s) = value.as_str() {
        serde_json::json!({"S":s})
    } else if let Some(n) = value.as_i64() {
        serde_json::json!({"N":n.to_string()})
    } else if let Some(b) = value.as_bool() {
        serde_json::json!({"BOOL":b})
    } else if value.is_null() {
        serde_json::json!({"NULL":true})
    } else if let Some(o) = value.as_object() {
        serde_json::json!({"M":dynamo_map(&Value::Object(o.clone()))?})
    } else {
        return Err(CodecError::Malformed);
    })
}
fn from_dynamo_map(value: &Value) -> Result<Value, CodecError> {
    let object = value.as_object().ok_or(CodecError::Malformed)?;
    object
        .iter()
        .map(|(k, v)| Ok((k.clone(), from_dynamo_attr(v)?)))
        .collect::<Result<serde_json::Map<_, _>, _>>()
        .map(Value::Object)
}
fn from_dynamo_attr(value: &Value) -> Result<Value, CodecError> {
    let o = value.as_object().ok_or(CodecError::Malformed)?;
    if let Some(v) = o.get("S").and_then(Value::as_str) {
        return Ok(Value::String(v.into()));
    }
    if let Some(v) = o.get("N").and_then(Value::as_str) {
        return serde_json::from_str(v).map_err(|_| CodecError::Malformed);
    }
    if let Some(v) = o.get("BOOL").and_then(Value::as_bool) {
        return Ok(Value::Bool(v));
    }
    if let Some(v) = o.get("B").and_then(Value::as_str) {
        if base64::engine::general_purpose::STANDARD.decode(v).is_err() {
            return Err(CodecError::Malformed);
        }
        // Keep binary values explicitly tagged in canonical JSON; decoding
        // them as text would silently corrupt the source representation.
        return Ok(serde_json::json!({ "$binary": v }));
    }
    if let Some(v) = o.get("SS").and_then(Value::as_array) {
        if !v.iter().all(Value::is_string) {
            return Err(CodecError::Malformed);
        }
        return Ok(Value::Array(v.clone()));
    }
    if let Some(v) = o.get("NS").and_then(Value::as_array) {
        let mut values = Vec::with_capacity(v.len());
        for number in v {
            let text = number.as_str().ok_or(CodecError::Malformed)?;
            values.push(serde_json::from_str(text).map_err(|_| CodecError::Malformed)?);
        }
        return Ok(Value::Array(values));
    }
    if let Some(v) = o.get("BS").and_then(Value::as_array) {
        if !v.iter().all(Value::is_string) {
            return Err(CodecError::Malformed);
        }
        return Ok(serde_json::json!({ "$binarySet": v }));
    }
    if o.get("NULL").is_some() {
        return Ok(Value::Null);
    }
    if let Some(v) = o.get("M") {
        return from_dynamo_map(v);
    }
    Err(CodecError::Malformed)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn roundtrips_jsonl_and_csv() {
        let records = vec![ImportRecord {
            collection: "users".into(),
            id: "a,1".into(),
            value: br#"{"name":"Ada"}"#.to_vec(),
        }];
        for f in [TransferFormat::Jsonl, TransferFormat::Csv] {
            assert_eq!(decode(&encode(&records, f).unwrap(), f, 1024).unwrap(), records);
        }
    }
    #[test]
    fn parses_dynamo_and_rejects_oversize() {
        let records = vec![ImportRecord {
            collection: String::new(),
            id: "a".into(),
            value: br#"{"n":1}"#.to_vec(),
        }];
        let encoded = encode(&records, TransferFormat::DynamoJson).unwrap();
        assert_eq!(decode(&encoded, TransferFormat::DynamoJson, 1024).unwrap(), records);
        assert_eq!(decode(&encoded, TransferFormat::DynamoJson, 1), Err(CodecError::Oversized));
    }

    #[test]
    fn preserves_dynamo_binary_metadata() {
        let input = br#"{"Item":{"id":{"S":"a"},"value":{"M":{"blob":{"B":"AQI="}}}}}"#;
        let records = decode(input, TransferFormat::DynamoJson, 1024).expect("dynamo record");
        assert_eq!(records[0].value, br#"{"blob":{"$binary":"AQI="}}"#);
    }

    #[test]
    fn preserves_dynamo_integers_beyond_f64_precision() {
        let input = br#"{"Item":{"id":{"S":"a"},"value":{"M":{"n":{"N":"9007199254740993"}}}}}"#;
        let records = decode(input, TransferFormat::DynamoJson, 1024).expect("dynamo record");
        assert_eq!(records[0].value, br#"{"n":9007199254740993}"#);
    }
}
