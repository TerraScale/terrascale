use serde_json::Value;
use terrascale::schema::{
    CollectionSchema, FieldDef, FieldType, IndexDef, MergePolicy, Schema, SchemaBuilder,
};
use terrascale::DocumentSchema;

#[allow(dead_code)]
#[derive(serde::Serialize, Schema)]
#[schema(collection = "accounts")]
struct Account {
    #[serde(rename = "display_name")]
    #[schema(indexed)]
    name: String,
    age: Option<i64>,
    payload: Vec<u8>,
    #[schema(unique)]
    email: String,
    #[schema(merge = "counter-pn")]
    visits: i64,
    #[serde(skip)]
    ignored: String,
}

#[allow(dead_code)]
#[derive(serde::Serialize, Schema)]
#[serde(rename_all = "camelCase")]
struct GenericRecord<T> {
    first_name: String,
    value: T,
    #[serde(skip_serializing)]
    write_only: String,
    #[serde(skip_deserializing)]
    read_only: String,
    #[serde(skip)]
    ignored: String,
}

#[test]
fn derive_emits_typed_fields_and_attributes() {
    let schema = Account::schema();
    assert_eq!(Account::collection(), "accounts");
    assert_eq!(
        schema
            .fields
            .iter()
            .map(|field| field.name.as_str())
            .collect::<Vec<_>>(),
        ["display_name", "age", "payload", "email", "visits"]
    );
    assert_eq!(schema.fields[0].field_type, FieldType::String);
    assert!(schema.fields[0].indexed);
    assert!(schema.fields[1].optional);
    assert_eq!(schema.fields[2].field_type, FieldType::Bytes);
    assert!(schema.fields[3].unique);
    assert_eq!(schema.fields[4].merge_policy, Some(FieldType::PnCounter));
}

#[test]
fn derive_preserves_serde_rename_all_skip_modes_and_generics() {
    let schema = <GenericRecord<u64> as DocumentSchema>::schema();
    assert_eq!(
        schema
            .fields
            .iter()
            .map(|field| field.name.as_str())
            .collect::<Vec<_>>(),
        ["firstName", "value", "writeOnly", "readOnly"]
    );
}

#[test]
fn schema_json_contains_indexes_ttl_history_and_merge_policy() {
    let collection = CollectionSchema::new("events")
        .field(FieldDef::required("expires_at", FieldType::Timestamp))
        .field(FieldDef::required("tenant", FieldType::String))
        .index(IndexDef::new("events_by_tenant", ["tenant"]))
        .ttl("expires_at")
        .history_window_days(30)
        .merge_policy(MergePolicy::HlcTiebreak);
    let json: Value = serde_json::from_str(&collection.to_json().expect("valid schema")).unwrap();
    assert_eq!(json["name"], "events");
    assert_eq!(json["historyRetentionDays"], 30);
    assert_eq!(json["mergePolicy"], "hlc-tiebreak");
    assert_eq!(json["ttl"]["field"], "expires_at");
    assert_eq!(json["indexes"][0]["fields"][0], "tenant");
    assert_eq!(json["fields"][0]["type"], "timestamp");
    assert_eq!(json["fields"][0]["required"], true);
}

#[test]
fn schema_validation_enforces_history_and_references() {
    let out_of_range = CollectionSchema::new("events").history_window_days(0);
    assert!(out_of_range.validate().is_err());

    let unknown_ttl = CollectionSchema::new("events").ttl("expires_at");
    assert!(unknown_ttl.validate().is_err());

    let unknown_index =
        CollectionSchema::new("events").index(IndexDef::new("events_by_missing", ["missing"]));
    assert!(unknown_index.validate().is_err());
}

#[test]
fn field_json_accepts_cross_sdk_required_shape() {
    let field: FieldDef = serde_json::from_value(serde_json::json!({
        "name": "name",
        "type": "string",
        "required": true,
        "indexed": true
    }))
    .expect("cross-SDK field JSON");
    assert!(!field.optional);
    assert!(field.indexed);

    let optional: FieldDef = serde_json::from_value(serde_json::json!({
        "name": "nickname",
        "type": "string",
        "optional": true
    }))
    .expect("legacy optional field JSON");
    assert!(optional.optional);

    let required_false: FieldDef = serde_json::from_value(serde_json::json!({
        "name": "nickname",
        "type": "string",
        "required": false
    }))
    .expect("optional required shape");
    assert!(required_false.optional);
}

#[test]
fn schema_validation_rejects_sql_unsafe_names() {
    assert!(CollectionSchema::new("bad-name").validate().is_err());
    assert!(CollectionSchema::new("users")
        .field(FieldDef::required("bad.name", FieldType::String))
        .validate()
        .is_err());
    assert!(CollectionSchema::new("users")
        .field(FieldDef::required("name", FieldType::String))
        .index(IndexDef::new("bad-name", ["name"]))
        .validate()
        .is_err());
    assert!(CollectionSchema::new("users")
        .field(FieldDef::required("expires_at", FieldType::Timestamp))
        .ttl("expires-at")
        .validate()
        .is_err());
    assert!(SchemaBuilder::new()
        .collection(CollectionSchema::new("bad-name"))
        .build()
        .is_err());
}

#[test]
fn schema_builder_serializes_a_root_schema_document() {
    let json: Value = serde_json::from_str(
        &SchemaBuilder::new()
            .collection(CollectionSchema::new("users"))
            .to_json()
            .expect("valid root schema"),
    )
    .unwrap();
    assert_eq!(json["collections"][0]["name"], "users");
}
