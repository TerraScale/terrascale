use super::*;

fn document(value: DocumentValue) -> Document {
    let mut document = Document::new(
        DocumentId::try_new("d").unwrap(),
        CollectionName::try_new("c").unwrap(),
        value,
    );
    document.set_version(7);
    document
}

#[test]
fn persisted_envelope_bytes_are_unchanged() {
    let value = DocumentValue::Object(BTreeMap::from([
        ("a".into(), DocumentValue::String("é😀\n\t\0\"\\".into())),
        ("f".into(), DocumentValue::Number(1e20)),
        ("i".into(), DocumentValue::Integer(i64::MAX)),
        ("n".into(), DocumentValue::Number(1.0)),
        ("z".into(), DocumentValue::Number(-0.0)),
    ]));
    let expected = r#"{"$collection":"c","$id":"d","$value":{"a":"é😀\u000a\u0009\u0000\"\\","f":100000000000000000000.0,"i":9223372036854775807,"n":1.0,"z":-0.0},"$version":7}"#;
    let encoded = encode_document_body(&document(value));
    assert_eq!(encoded, expected.as_bytes());
    let decoded = decode_document_body(&encoded).unwrap();
    assert_eq!(encode_document_body(&decoded), encoded);
}

#[test]
fn floating_point_bits_survive_storage() {
    for bits in [
        0,
        (-0.0_f64).to_bits(),
        1,
        f64::MIN_POSITIVE.to_bits(),
        f64::MAX.to_bits(),
        (0.1_f64).to_bits(),
        f64::INFINITY.to_bits(),
        f64::NEG_INFINITY.to_bits(),
        0x7ff8_0000_0000_0042,
        0xfff8_0000_0000_0001,
    ] {
        let original = document(DocumentValue::Number(f64::from_bits(bits)));
        let bytes = encode_document_body(&original);
        let decoded = decode_document_body(&bytes).unwrap();
        let DocumentValue::Number(value) = decoded.value() else {
            panic!("numeric variant changed")
        };
        assert_eq!(value.to_bits(), bits);
    }
    let bytes = encode_document_body(&document(DocumentValue::Number(f64::INFINITY)));
    assert_eq!(
        bytes,
        br#"{"$collection":"c","$id":"d","$value":{"__terrascale_nonfinite__":"7ff0000000000000"},"$version":7}"#,
    );
}

#[test]
fn json_syntax_unicode_and_envelope_validation_use_serde() {
    let body =
        |value: &str| format!(r#"{{"$collection":"c","$id":"d","$value":{value},"$version":7}}"#);
    let decoded = decode_document_body(body(r#""\ud83d\ude00""#).as_bytes()).unwrap();
    assert_eq!(decoded.value(), &DocumentValue::String("😀".into()));
    for invalid in ["01", "+1", "1.", "1e9999", "[1,]", r#""\ud800""#, r#""\ude00""#] {
        assert!(decode_document_body(body(invalid).as_bytes()).is_none(), "accepted {invalid}");
    }
    let valid = body("null");
    assert!(decode_document_body(format!("{valid} \n\t").as_bytes()).is_some());
    assert!(decode_document_body(format!("{valid} null").as_bytes()).is_none());
    assert!(
        decode_document_body(valid.replace("\"$version\":7", "\"$version\":-1").as_bytes())
            .is_none()
    );
    assert!(
        decode_document_body(valid.replace("\"$version\":7", "\"$version\":7.0").as_bytes())
            .is_none()
    );
    assert!(
        decode_document_body(
            valid.replace("\"$version\":7", "\"extra\":0,\"$version\":7").as_bytes()
        )
        .is_none()
    );
    let large = decode_document_body(body("9223372036854775808").as_bytes()).unwrap();
    assert!(matches!(large.value(), DocumentValue::Number(_)));
}

#[test]
fn depth_limit_is_checked_before_building_deeper_values() {
    let nested = |depth| {
        let mut value = DocumentValue::Null;
        for _ in 0..depth {
            value = DocumentValue::Array(vec![value]);
        }
        encode_document_body(&document(value))
    };
    // The document envelope occupies depth zero; $value starts at depth one.
    assert!(decode_document_body(&nested(codec::MAX_VALUE_DEPTH - 1)).is_some());
    assert!(decode_document_body(&nested(codec::MAX_VALUE_DEPTH)).is_none());
}

#[test]
fn nonfinite_marker_requires_the_exact_reserved_shape() {
    let ordinary = DocumentValue::Object(BTreeMap::from([
        (NONFINITE_FIELD.into(), DocumentValue::String("7ff0000000000000".into())),
        ("other".into(), DocumentValue::Boolean(true)),
    ]));
    let decoded = decode_document_body(&encode_document_body(&document(ordinary.clone()))).unwrap();
    assert_eq!(decoded.value(), &ordinary);
    let finite_marker = DocumentValue::Object(BTreeMap::from([(
        NONFINITE_FIELD.into(),
        DocumentValue::String("3ff0000000000000".into()),
    )]));
    let decoded =
        decode_document_body(&encode_document_body(&document(finite_marker.clone()))).unwrap();
    assert_eq!(decoded.value(), &finite_marker);
}
