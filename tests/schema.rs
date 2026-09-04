use serde_json::Value;
use terrascale::schema::{
    CollectionSchema, FieldDef, FieldType, IndexDef, IndexKind, MergePolicy, Schema, SchemaBuilder,
    SchemaDocument,
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
        .index(IndexDef::fts("events_search", "tenant"))
        .ttl("expires_at")
        .history_window_days(30)
        .merge_policy(MergePolicy::HlcTiebreak);
    let json: Value = serde_json::from_str(&collection.to_json().expect("valid schema")).unwrap();
    assert_eq!(json["name"], "events");
    assert_eq!(json["historyRetentionDays"], 30);
    assert_eq!(json["mergePolicy"], "hlc-tiebreak");
    assert_eq!(json["ttl"]["field"], "expires_at");
    assert_eq!(json["indexes"][0]["fields"][0], "tenant");
    assert_eq!(json["indexes"][0]["kind"], "btree");
    assert_eq!(json["indexes"][1]["kind"], "fts");
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
fn field_json_accepts_only_the_canonical_required_shape() {
    let field: FieldDef = serde_json::from_value(serde_json::json!({
        "name": "name",
        "type": "string",
        "required": true,
        "indexed": true
    }))
    .expect("cross-SDK field JSON");
    assert!(!field.optional);
    assert!(field.indexed);

    let optional = serde_json::from_value::<FieldDef>(serde_json::json!({
        "name": "nickname",
        "type": "string",
        "optional": true
    }));
    assert!(optional.is_err());

    let required_false: FieldDef = serde_json::from_value(serde_json::json!({
        "name": "nickname",
        "type": "string",
        "required": false
    }))
    .expect("optional required shape");
    assert!(required_false.optional);

    assert!(serde_json::from_value::<FieldDef>(serde_json::json!({
        "name": "name",
        "field_type": "string",
    }))
    .is_err());
}

#[test]
fn schema_vector_is_exact_and_all_declaration_orders_are_canonical() {
    let first = SchemaBuilder::new()
        .collection(
            CollectionSchema::new("zeta")
                .field(FieldDef::optional("title", FieldType::Text))
                .field(FieldDef::required("id", FieldType::String).unique())
                .index(IndexDef::fts("title_fts", "title"))
                .index(IndexDef::new("by_id", ["id"])),
        )
        .collection(CollectionSchema::new("alpha"))
        .to_json()
        .expect("valid schema");
    let second = SchemaBuilder::new()
        .collection(CollectionSchema::new("alpha"))
        .collection(
            CollectionSchema::new("zeta")
                .field(FieldDef::optional("title", FieldType::Text))
                .field(FieldDef::required("id", FieldType::String).unique())
                .index(IndexDef::fts("title_fts", "title"))
                .index(IndexDef::new("by_id", ["id"])),
        )
        .to_json()
        .expect("valid schema");
    assert_eq!(first, second);
    assert_eq!(
        first,
        r#"{"collections":[{"name":"alpha","fields":[],"historyRetentionDays":7,"mergePolicy":"remove-wins"},{"name":"zeta","fields":[{"name":"title","type":"text","required":false,"indexed":false},{"name":"id","type":"string","required":true,"indexed":true,"unique":true}],"indexes":[{"name":"title_fts","fields":["title"],"kind":"fts"},{"name":"by_id","fields":["id"],"kind":"btree"}],"historyRetentionDays":7,"mergePolicy":"remove-wins"}]}"#
    );
}

#[test]
fn collection_and_field_fts_authoring_forms_normalize_to_indexes() {
    let collection = CollectionSchema::new("docs")
        .field(FieldDef::required("body", FieldType::Text).fts())
        .field(FieldDef::required("title", FieldType::String))
        .full_text("title");
    assert_eq!(
        collection.to_json().expect("valid FTS schema"),
        r#"{"name":"docs","fields":[{"name":"body","type":"text","required":true,"indexed":false},{"name":"title","type":"string","required":true,"indexed":false}],"indexes":[{"name":"title_fts","fields":["title"],"kind":"fts"},{"name":"body_fts","fields":["body"],"kind":"fts"}],"historyRetentionDays":7,"mergePolicy":"remove-wins"}"#
    );

    let from_json: CollectionSchema = serde_json::from_value(serde_json::json!({
        "name": "docs",
        "fields": [
            {"name": "body", "type": "text", "required": true, "fts": true},
            {"name": "title", "type": "string", "required": true}
        ],
        "fts": ["title"],
        "historyRetentionDays": 7,
        "mergePolicy": "remove-wins"
    }))
    .expect("authoring FTS forms");
    assert_eq!(
        from_json.to_json().expect("canonical FTS schema"),
        collection.to_json().expect("same schema")
    );
}

#[test]
fn fts_requires_one_string_or_text_field_and_is_not_unique() {
    let collection = CollectionSchema::new("docs")
        .field(FieldDef::required("title", FieldType::Text))
        .field(FieldDef::required("count", FieldType::Integer));
    assert!(collection
        .clone()
        .index(IndexDef {
            name: "bad".into(),
            fields: vec!["title".into(), "count".into()],
            unique: false,
            kind: IndexKind::Fts,
        })
        .validate()
        .is_err());
    assert!(collection
        .clone()
        .index(IndexDef {
            name: "bad".into(),
            fields: vec!["count".into()],
            unique: false,
            kind: IndexKind::Fts,
        })
        .validate()
        .is_err());
    assert!(collection
        .index(IndexDef {
            name: "bad".into(),
            fields: vec!["title".into()],
            unique: true,
            kind: IndexKind::Fts,
        })
        .validate()
        .is_err());
}

#[test]
fn schema_document_serialization_is_canonical_and_rejects_wrong_forms() {
    let document: SchemaDocument = serde_json::from_value(serde_json::json!({
        "collections": [{
            "name": "docs",
            "fields": [{"name": "body", "type": "text", "required": false}],
            "indexes": [{"name": "body_fts", "fields": ["body"], "kind": "fts"}],
            "historyRetentionDays": 1,
            "mergePolicy": "last-write-wins"
        }]
    }))
    .expect("canonical schema vector");
    assert_eq!(
        serde_json::to_string(&document).expect("serialize schema"),
        r#"{"collections":[{"name":"docs","fields":[{"name":"body","type":"text","required":false,"indexed":false}],"indexes":[{"name":"body_fts","fields":["body"],"kind":"fts"}],"historyRetentionDays":1,"mergePolicy":"last-write-wins"}]}"#
    );
    assert!(serde_json::from_value::<SchemaDocument>(serde_json::json!({
        "collections": [{
            "name": "docs",
            "fields": [{"name": "body", "type": "text"}],
            "indexes": [{"name": "body_fts", "fields": ["body"], "kind": "full-text"}],
            "historyRetentionDays": 7,
            "mergePolicy": "remove-wins"
        }]
    }))
    .is_err());
    assert!(serde_json::from_value::<SchemaDocument>(serde_json::json!({
        "collections": [{
            "name": "docs",
            "fields": [],
            "historyWindowDays": 7,
            "mergePolicy": "remove-wins"
        }]
    }))
    .is_err());
    assert!(serde_json::from_value::<SchemaDocument>(serde_json::json!({
        "collections": [{
            "name": "docs",
            "fields": [],
            "historyRetentionDays": 7,
            "merge_policy": "remove-wins"
        }]
    }))
    .is_err());
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
