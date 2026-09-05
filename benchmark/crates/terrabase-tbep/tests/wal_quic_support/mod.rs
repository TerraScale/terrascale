use rcgen::{
    BasicConstraints, Certificate, CertificateParams, ExtendedKeyUsagePurpose, Ia5String, IsCa,
    KeyPair, KeyUsagePurpose, SanType,
};
use rustls_pki_types::{CertificateDer, PrivateKeyDer, PrivatePkcs8KeyDer};
use std::net::{Ipv4Addr, UdpSocket};
use std::time::Duration;
use terrabase_member_transport::production::{
    MemberWorkloadIdentity, ProductionPeerAdmission, ProductionQuicEndpoint, ProductionRegion,
    ProductionTlsMaterial, ProductionTransportConfig, ProductionTransportLimits,
};
fn member_tls_material(
    identity: &MemberWorkloadIdentity,
    server_name: &str,
    issuer: &Certificate,
    issuer_key: &KeyPair,
    anchor: &CertificateDer<'static>,
) -> ProductionTlsMaterial {
    let key = KeyPair::generate().expect("member key");
    let mut params =
        CertificateParams::new(vec![server_name.to_owned()]).expect("member certificate");
    params.subject_alt_names.push(SanType::URI(
        Ia5String::try_from(identity.as_str()).expect("canonical identity is IA5 text"),
    ));
    params.extended_key_usages =
        vec![ExtendedKeyUsagePurpose::ServerAuth, ExtendedKeyUsagePurpose::ClientAuth];
    let certificate = params.signed_by(&key, issuer, issuer_key).expect("signed member leaf");
    ProductionTlsMaterial::try_new(
        vec![CertificateDer::from(certificate.der().to_vec())],
        PrivateKeyDer::Pkcs8(PrivatePkcs8KeyDer::from(key.serialize_der())),
        vec![anchor.clone()],
    )
    .expect("complete member TLS material")
}
pub fn endpoint_pair() -> (ProductionQuicEndpoint, ProductionQuicEndpoint) {
    let first_socket = UdpSocket::bind((Ipv4Addr::LOCALHOST, 0)).expect("first port");
    let second_socket = UdpSocket::bind((Ipv4Addr::LOCALHOST, 0)).expect("second port");
    let first_address = first_socket.local_addr().expect("first address");
    let second_address = second_socket.local_addr().expect("second address");
    drop(first_socket);
    drop(second_socket);

    let issuer_key = KeyPair::generate().expect("issuer key");
    let mut issuer_params = CertificateParams::new(Vec::new()).expect("issuer parameters");
    issuer_params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
    issuer_params.key_usages =
        vec![KeyUsagePurpose::DigitalSignature, KeyUsagePurpose::KeyCertSign];
    let issuer = issuer_params.self_signed(&issuer_key).expect("issuer certificate");
    let anchor = CertificateDer::from(issuer.der().to_vec());

    let first_identity =
        MemberWorkloadIdentity::try_new("prod", "cluster-a", "01ARZ3NDEKTSV4RRFFQ69G5FAV")
            .expect("first identity");
    let second_identity =
        MemberWorkloadIdentity::try_new("prod", "cluster-a", "01ARZ3NDEKTSV4RRFFQ69G5FAW")
            .expect("second identity");
    let first_name = "member-a.internal";
    let second_name = "member-b.internal";
    let first_tls = member_tls_material(&first_identity, first_name, &issuer, &issuer_key, &anchor);
    let second_tls =
        member_tls_material(&second_identity, second_name, &issuer, &issuer_key, &anchor);

    let limits = ProductionTransportLimits::try_new(
        2 * 1024 * 1024,
        8,
        8,
        4 * 1024 * 1024,
        Duration::from_secs(5),
        Duration::from_secs(5),
        Duration::from_secs(10),
        Duration::from_secs(5),
    )
    .expect("integration limits");
    let first_config = ProductionTransportConfig::try_new(
        first_address,
        first_identity.clone(),
        ProductionRegion::Gru,
        9,
        12,
        vec![
            ProductionPeerAdmission::try_new(
                second_identity.clone(),
                ProductionRegion::Gru,
                second_address,
                second_name,
            )
            .expect("second peer"),
        ],
        limits.clone(),
    )
    .expect("first config");
    let second_config = ProductionTransportConfig::try_new(
        second_address,
        second_identity.clone(),
        ProductionRegion::Gru,
        9,
        12,
        vec![
            ProductionPeerAdmission::try_new(
                first_identity.clone(),
                ProductionRegion::Gru,
                first_address,
                first_name,
            )
            .expect("first peer"),
        ],
        limits,
    )
    .expect("second config");

    let first = ProductionQuicEndpoint::bind(first_config, first_tls).expect("first endpoint");
    let second = ProductionQuicEndpoint::bind(second_config, second_tls).expect("second endpoint");
    (first, second)
}

pub async fn withhold_receipt(
    connection: &terrabase_member_transport::production::ProductionConnection,
    binding: &terrabase_cluster::wal_route::WalReplicaBinding,
    follower: &std::sync::Mutex<terrabase_cluster::wal_follower::WalFollower>,
    durable: tokio::sync::oneshot::Sender<()>,
) -> terrabase_cluster::wal_wire::ScopedWalAck {
    let bytes = connection.receive_frame().await.unwrap();
    let batch = terrabase_cluster::wal_wire::ScopedWalBatch::decode(&bytes).unwrap();
    let receipt = binding.apply(connection.peer(), &batch, &mut follower.lock().unwrap()).unwrap();
    durable.send(()).unwrap();
    receipt
}
