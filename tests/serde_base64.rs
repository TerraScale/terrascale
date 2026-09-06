use serde::{Deserialize, Serialize};

#[derive(Debug, PartialEq, Serialize, Deserialize)]
struct Document {
    #[serde(with = "terrascale::serde_base64")]
    bytes: Vec<u8>,
    #[serde(default, with = "terrascale::serde_base64::option")]
    optional: Option<Vec<u8>>,
}

#[test]
fn bytes_fields_use_canonical_base64_and_preserve_optional_values() {
    let value = Document {
        bytes: vec![0, 255],
        optional: Some(vec![255]),
    };
    let json = r#"{"bytes":"AP8=","optional":"/w=="}"#;
    assert_eq!(serde_json::to_string(&value).unwrap(), json);
    assert_eq!(serde_json::from_str::<Document>(json).unwrap(), value);
    assert_eq!(
        serde_json::from_str::<Document>(r#"{"bytes":""}"#).unwrap(),
        Document {
            bytes: vec![],
            optional: None
        }
    );
    assert_eq!(
        serde_json::from_str::<Document>(r#"{"bytes":"","optional":null}"#)
            .unwrap()
            .optional,
        None
    );
    for json in [
        r#"{"bytes":[0,255]}"#,
        r#"{"bytes":"AP8"}"#,
        r#"{"bytes":"_w=="}"#,
        r#"{"bytes":"AB=="}"#,
        r#"{"bytes":"","optional":[1]}"#,
    ] {
        assert!(
            serde_json::from_str::<Document>(json).is_err(),
            "accepted noncanonical {json}"
        );
    }
}
