//! Lossless scalar, filter and patch encoding checks.

use terrascale::v1::*;

#[test]
fn ids_names_and_builder_validation() {
    assert_eq!(
        ulid_for("users", "ana@example.com").to_string(),
        "2XXRVAPY9W9Y52V50008HFMDXY"
    );
    assert!(is_valid_name("users") && is_valid_name("a-b_9"));
    assert!(!is_valid_name("Users") && !is_valid_name("9a") && !is_valid_name(""));
    assert!(Version::new("tbv1-0123456789abcdef0123456789abcdef").is_ok());
    assert!(Version::new("tbv1-0123456789ABCDEF0123456789abcdef").is_err());
    let token = "fixture-access-token";
    let bad = |url: &str, tok: &str| {
        matches!(
            Client::builder(url, tok)
                .project("01ARZ3NDEKTSV4RRFFQ69G5FAV")
                .build(),
            Err(Error::InvalidInput(_))
        )
    };
    assert!(bad("https://example.com", ""));
    assert!(bad("http://example.com", token));
    assert!(bad("https://example.com?x=1", token));
    assert!(bad("ftp://example.com", token));
    assert!(Client::builder("http://127.0.0.1:1", token)
        .project("01ARZ3NDEKTSV4RRFFQ69G5FAV")
        .build()
        .is_ok());
    assert!(Client::builder("https://example.com/prefix/", token)
        .project("01ARZ3NDEKTSV4RRFFQ69G5FAV")
        .build()
        .is_ok());
    assert!(matches!(
        Client::builder("https://example.com", token)
            .project("01ARZ3NDEKTSV4RRFFQ69G5FAV")
            .add_root_ca_pem("not a pem")
            .build(),
        Err(Error::InvalidInput(_))
    ));
}

#[test]
fn numbers_and_filters_encode_exactly() {
    let n: Number = "123456789012345678901234567890.000000000000000000001"
        .parse()
        .unwrap();
    assert_eq!(
        serde_json::to_string(&n).unwrap(),
        "123456789012345678901234567890.000000000000000000001"
    );
    let back: Number = serde_json::from_str("1e400").unwrap();
    assert_eq!(back.as_str(), "1e400");
    assert!("01".parse::<Number>().is_err() && "1.".parse::<Number>().is_err());
    assert!(serde_json::from_str::<Number>("\"1\"").is_err());

    let f = Filter::and([
        Filter::eq("/email", "Ana@Example.com"),
        Filter::gte("/n", n.clone()),
        Filter::in_("$id", [ulid_for("users", "a")]),
        Filter::not(Filter::exists("/deleted_at")),
    ]);
    assert_eq!(
        serde_json::to_string(&f).unwrap(),
        format!(
            r#"{{"and":[{{"eq":["/email","Ana@Example.com"]}},{{"gte":["/n",{n}]}},{{"in":["$id",["{}"]]}},{{"not":{{"exists":"/deleted_at"}}}}]}}"#,
            ulid_for("users", "a")
        )
    );
    assert!(Filter::and([]).validate().is_err());
    assert!(Filter::lt("/n", Scalar::Null).validate().is_err());
    assert!(Filter::from_json("[1]").is_err());
    let raw = Filter::from_json(r#"{"eq":["/a",1]}"#).unwrap();
    assert_eq!(serde_json::to_string(&raw).unwrap(), r#"{"eq":["/a",1]}"#);
    assert_eq!(raw, Filter::eq("/a", 1));
    let ops = Patch::ops(vec![
        PatchOp::set("/profile/name", "Ana").unwrap(),
        PatchOp::unset("/legacy"),
        PatchOp::inc("/db_count", 1),
        PatchOp::append("/tags", "beta").unwrap(),
    ]);
    let Patch::Ops(list) = ops else {
        unreachable!()
    };
    assert_eq!(
        serde_json::to_string(&list).unwrap(),
        r#"[{"op":"set","path":"/profile/name","value":"Ana"},{"op":"unset","path":"/legacy"},{"op":"inc","path":"/db_count","by":1},{"op":"append","path":"/tags","value":"beta"}]"#
    );
}
