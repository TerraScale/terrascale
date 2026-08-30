use flatbuffers::FlatBufferBuilder;
use tbep_fbs::tbep_generated::terrabase::tbep as fbs;

#[test]
fn probe_union_with_other_fields() {
    // Envelope (7 fields incl. union body) -> SchemaValidateRequest (2 fields incl. union document)
    // -> ValueString (single string). Union nested in tables with other fields.
    let mut b = FlatBufferBuilder::new();
    let text = b.create_string("probe document text");
    let value = fbs::ValueString::create(&mut b, &fbs::ValueStringArgs { value: Some(text) });
    let req = fbs::SchemaValidateRequest::create(
        &mut b,
        &fbs::SchemaValidateRequestArgs {
            collection: None,
            document_type: fbs::TbepValue::ValueString,
            document: Some(value.as_union_value()),
        },
    );
    let env = fbs::TbepEnvelope::create(
        &mut b,
        &fbs::TbepEnvelopeArgs {
            protocol_major: 1,
            protocol_minor: 0,
            required_features: Default::default(),
            operation: tbep_fbs::TbepOperation::SCHEMA_VALIDATE,
            correlation: Some(&fbs::Ulid([1; 16])),
            causation: Some(&fbs::Ulid([2; 16])),
            body_type: fbs::TbepFrame::SchemaValidateRequest,
            body: Some(req.as_union_value()),
        },
    );
    b.finish_minimal(env);
    let data = b.finished_data();
    match flatbuffers::root::<fbs::TbepEnvelope>(data) {
        Ok(e) => println!(
            "union+fields OK len={} text={:?}",
            data.len(),
            e.body_as_schema_validate_request()
                .unwrap()
                .document_as_value_string()
                .unwrap()
                .value()
        ),
        Err(e) => println!("union+fields ERR len={} {e}", data.len()),
    }
}

#[test]
fn probe_union_only_wrapper() {
    let mut b = FlatBufferBuilder::new();
    let text = b.create_string("cell");
    let value = fbs::ValueString::create(&mut b, &fbs::ValueStringArgs { value: Some(text) });
    let cell = fbs::ValueCell::create(
        &mut b,
        &fbs::ValueCellArgs {
            value_type: fbs::TbepValue::ValueString,
            value: Some(value.as_union_value()),
        },
    );
    let env = fbs::TbepEnvelope::create(
        &mut b,
        &fbs::TbepEnvelopeArgs {
            protocol_major: 1,
            protocol_minor: 0,
            required_features: Default::default(),
            operation: tbep_fbs::TbepOperation::SCHEMA_VALIDATE,
            correlation: Some(&fbs::Ulid([1; 16])),
            causation: Some(&fbs::Ulid([2; 16])),
            body_type: fbs::TbepFrame::SchemaValidateRequest,
            body: Some(cell.as_union_value()),
        },
    );
    b.finish_minimal(env);
    let data = b.finished_data();
    match flatbuffers::root::<fbs::TbepEnvelope>(data) {
        Ok(_) => println!("wrapper OK len={}", data.len()),
        Err(e) => println!("wrapper ERR len={} {e}", data.len()),
    }
}
