//! Name and client builder validation.

use terrascale::v1::*;

#[test]
fn names_and_builder_validation() {
    assert!(is_valid_name("users") && is_valid_name("a-b_9"));
    assert!(!is_valid_name("Users") && !is_valid_name("9a") && !is_valid_name(""));
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
