use terrascale::{ErrorKind, ReqwestTransport};

#[test]
fn invalid_custom_ca_fails_before_transport() {
    for pem in [
        &b""[..],
        &b"not a PEM certificate"[..],
        &b"-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n"[..],
    ] {
        let error = ReqwestTransport::with_root_certificates(pem).unwrap_err();
        assert_eq!(error.kind, ErrorKind::Validation);
        assert_eq!(error.code, "invalid_root_certificate");
    }
}
