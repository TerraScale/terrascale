use crate::{
    MAX_DECODED_APPLICATION_BYTES, MAX_ENCODED_APPLICATION_BYTES, MAX_EVENT_METADATA_BYTES,
    MAX_FRONTIERS_PER_HANDSHAKE, MAX_GRPC_MESSAGE_BYTES, MAX_IN_FLIGHT_BATCHES_PER_PEER,
    MAX_IN_FLIGHT_BYTES_PER_PEER, MAX_OPEN_STREAMS_PER_PEER, MAX_TRANSACTION_GROUP_BYTES,
    ProtocolError, V1_EVENT_FORMAT, V1_PROTOCOL_MAJOR, V1_PROTOCOL_MINOR,
    v1::{self, PayloadCodec},
};

/// Local compatibility and resource capabilities used to answer a peer hello.
#[derive(Clone, Debug)]
pub struct NegotiationPolicy {
    /// This node's stable identity and certified shard configuration.
    pub local_identity: v1::PeerIdentity,
    /// Exact protocol version selected by this implementation.
    pub protocol_version: v1::ProtocolVersion,
    /// Supported committed-event format version.
    pub event_format_version: u32,
    /// Supported binary record format versions. The highest common scalar
    /// version is selected for the entire stream.
    pub record_format_versions: Vec<u32>,
    /// Supported merge policy version.
    pub merge_policy_version: u32,
    /// Codec preference order, first item preferred.
    pub codecs: Vec<PayloadCodec>,
    /// Maximum transfer limits and initial inbound byte credit offered to peer.
    pub limits: v1::TransferLimits,
    /// Local durable/applied progress advertised to the peer.
    pub frontiers: Vec<v1::PeerFrontier>,
}

/// Validates and negotiates a v1 handshake without creating a replication ACK.
///
/// The caller must authenticate `hello.local_identity` against the connection's
/// mTLS peer certificates before calling this function. This only performs
/// protocol and epoch/configuration compatibility checks.
///
/// # Errors
///
/// Returns [`ProtocolError::Invalid`] when identity, version, or codec
/// negotiation is incompatible, and [`ProtocolError::LimitExceeded`] when a
/// negotiated transfer limit exceeds the local v1 ceiling.
pub fn negotiate_hello(
    hello: &v1::Hello,
    policy: &NegotiationPolicy,
) -> Result<v1::Welcome, ProtocolError> {
    validate_peer_identity(&policy.local_identity)?;
    validate_transfer_limits(&policy.limits)?;
    validate_frontiers(&policy.frontiers)?;
    let remote = hello
        .local_identity
        .as_ref()
        .ok_or(ProtocolError::Invalid("missing peer identity"))?;
    validate_peer_identity(remote)?;
    validate_frontiers(&hello.frontiers)?;
    validate_transfer_limit_offer(hello.limits.as_ref())?;

    validate_peer_pair(
        &policy.local_identity,
        remote,
        hello.expected_remote_member_id.as_ref(),
    )?;

    let protocol = policy.protocol_version;
    let record_format_version = validate_version_compatibility(hello, policy, protocol)?;
    let negotiated_codecs = negotiate_codecs(&policy.codecs, &hello.codecs)?;

    let negotiated_limits = negotiate_limits(&policy.limits, hello.limits.as_ref())?;
    Ok(v1::Welcome {
        local_identity: Some(policy.local_identity.clone()),
        protocol_version: Some(protocol),
        event_format_version: policy.event_format_version,
        record_format_version,
        merge_policy_version: policy.merge_policy_version,
        codecs: negotiated_codecs
            .into_iter()
            .map(|codec| codec as i32)
            .collect(),
        limits: Some(negotiated_limits),
        frontiers: policy.frontiers.clone(),
    })
}

fn validate_peer_pair(
    local: &v1::PeerIdentity,
    remote: &v1::PeerIdentity,
    expected_remote_member_id: &[u8],
) -> Result<(), ProtocolError> {
    if expected_remote_member_id.len() != 16
        || expected_remote_member_id != local.member_id.as_ref()
    {
        return Err(ProtocolError::Invalid("unexpected remote member identity"));
    }
    if remote.member_id == local.member_id
        || remote.tenant_id != local.tenant_id
        || remote.database_id != local.database_id
        || remote.shard_id != local.shard_id
    {
        return Err(ProtocolError::Invalid(
            "peer identity is outside the local shard",
        ));
    }
    if remote.membership_epoch != local.membership_epoch
        || remote.configuration_id != local.configuration_id
    {
        return Err(ProtocolError::Invalid(
            "membership epoch/configuration mismatch",
        ));
    }
    Ok(())
}

fn validate_version_compatibility(
    hello: &v1::Hello,
    policy: &NegotiationPolicy,
    protocol: v1::ProtocolVersion,
) -> Result<u32, ProtocolError> {
    if protocol.major != V1_PROTOCOL_MAJOR || protocol.minor != V1_PROTOCOL_MINOR {
        return Err(ProtocolError::Invalid("unsupported local protocol version"));
    }
    if !range_contains(
        hello.protocol_versions.as_ref(),
        protocol.major,
        protocol.minor,
    )? {
        return Err(ProtocolError::Invalid("no common protocol version"));
    }
    if !range_contains(
        hello.event_format_versions.as_ref(),
        policy.event_format_version,
        0,
    )? || policy.event_format_version != V1_EVENT_FORMAT
    {
        return Err(ProtocolError::Invalid("no common event format"));
    }
    let record_format_version = select_highest_common_record_format(
        hello.record_format_versions.as_ref(),
        &policy.record_format_versions,
    )?;
    if hello.minimum_merge_policy_version == 0
        || hello.maximum_merge_policy_version < hello.minimum_merge_policy_version
        || policy.merge_policy_version < hello.minimum_merge_policy_version
        || policy.merge_policy_version > hello.maximum_merge_policy_version
    {
        return Err(ProtocolError::Invalid("no common merge policy"));
    }
    Ok(record_format_version)
}

fn select_highest_common_record_format(
    peer_range: Option<&v1::VersionRange>,
    local_versions: &[u32],
) -> Result<u32, ProtocolError> {
    if local_versions.is_empty() {
        return Err(ProtocolError::Invalid("no local record formats"));
    }
    for (index, version) in local_versions.iter().enumerate() {
        if *version == 0 || local_versions[..index].contains(version) {
            return Err(ProtocolError::Invalid("invalid local record formats"));
        }
    }

    let mut selected = None;
    for version in local_versions {
        if range_contains(peer_range, *version, 0)? {
            selected = Some(selected.map_or(*version, |current: u32| current.max(*version)));
        }
    }
    selected.ok_or(ProtocolError::Invalid("no common record format"))
}

fn negotiate_codecs(
    local_codecs: &[PayloadCodec],
    peer_codecs: &[i32],
) -> Result<Vec<PayloadCodec>, ProtocolError> {
    validate_negotiated_codecs(local_codecs)?;
    if peer_codecs.len() > 2 {
        return Err(ProtocolError::Invalid("too many peer application codecs"));
    }
    let mut decoded_peer_codecs = Vec::with_capacity(peer_codecs.len());
    for wire_codec in peer_codecs {
        let codec = PayloadCodec::try_from(*wire_codec)
            .map_err(|_| ProtocolError::Invalid("unknown peer application codec"))?;
        if decoded_peer_codecs.contains(&codec) {
            return Err(ProtocolError::Invalid("duplicate peer application codec"));
        }
        decoded_peer_codecs.push(codec);
    }
    let negotiated = local_codecs
        .iter()
        .copied()
        .filter(|codec| decoded_peer_codecs.contains(codec))
        .collect::<Vec<_>>();
    if negotiated.is_empty() {
        return Err(ProtocolError::Invalid("no common application codec"));
    }
    Ok(negotiated)
}

pub(crate) fn validate_negotiated_codecs(codecs: &[PayloadCodec]) -> Result<(), ProtocolError> {
    if codecs.is_empty() {
        return Err(ProtocolError::Invalid("empty negotiated codec set"));
    }
    if codecs.len() > 2 {
        return Err(ProtocolError::Invalid(
            "too many negotiated application codecs",
        ));
    }
    for (index, codec) in codecs.iter().enumerate() {
        if !matches!(codec, PayloadCodec::Identity | PayloadCodec::Lz4BlockV1) {
            return Err(ProtocolError::Invalid(
                "unsupported negotiated application codec",
            ));
        }
        if codecs[..index].contains(codec) {
            return Err(ProtocolError::Invalid(
                "duplicate negotiated application codec",
            ));
        }
    }
    Ok(())
}

/// Validates the stable identifier widths and nonzero configuration epoch.
///
/// # Errors
///
/// Returns [`ProtocolError::Invalid`] when any fixed-width identity or
/// certified configuration field is malformed.
pub fn validate_peer_identity(identity: &v1::PeerIdentity) -> Result<(), ProtocolError> {
    if identity.tenant_id.len() != 16
        || identity.database_id.len() != 16
        || identity.member_id.len() != 16
        || identity.member_incarnation.len() != 16
        || identity.member_incarnation.iter().all(|byte| *byte == 0)
        || identity.membership_epoch == 0
        || identity.configuration_id.len() != 32
        || identity.configuration_id.iter().all(|byte| *byte == 0)
    {
        return Err(ProtocolError::Invalid("peer identity or configuration"));
    }
    Ok(())
}

/// Validates bounded frontier count, origin identifiers, and applied ≤ durable.
///
/// # Errors
///
/// Returns [`ProtocolError::Invalid`] for malformed or duplicate origin
/// frontiers and [`ProtocolError::LimitExceeded`] when the frontier cap is exceeded.
pub fn validate_frontiers(frontiers: &[v1::PeerFrontier]) -> Result<(), ProtocolError> {
    if frontiers.len() > MAX_FRONTIERS_PER_HANDSHAKE {
        return Err(ProtocolError::LimitExceeded("frontier count"));
    }
    for (index, frontier) in frontiers.iter().enumerate() {
        let origin = frontier
            .origin
            .as_ref()
            .ok_or(ProtocolError::Invalid("missing frontier origin"))?;
        if origin.replica_id.len() != 16
            || origin.incarnation.len() != 16
            || origin.incarnation.iter().all(|byte| *byte == 0)
        {
            return Err(ProtocolError::Invalid("frontier origin identity"));
        }
        if frontier.applied_sequence.is_some_and(|applied| {
            frontier
                .durable_sequence
                .is_none_or(|durable| applied > durable)
        }) {
            return Err(ProtocolError::Invalid("applied frontier exceeds durable"));
        }
        if frontiers[..index].iter().any(|previous| {
            previous.origin.as_ref().is_some_and(|previous_origin| {
                previous_origin.replica_id == origin.replica_id
                    && previous_origin.incarnation == origin.incarnation
            })
        }) {
            return Err(ProtocolError::Invalid("duplicate origin frontier"));
        }
    }
    Ok(())
}

fn range_contains(
    range: Option<&v1::VersionRange>,
    major: u32,
    minor: u32,
) -> Result<bool, ProtocolError> {
    let range = range.ok_or(ProtocolError::Invalid("missing version range"))?;
    let minimum = range
        .minimum
        .as_ref()
        .ok_or(ProtocolError::Invalid("missing minimum version"))?;
    let maximum = range
        .maximum
        .as_ref()
        .ok_or(ProtocolError::Invalid("missing maximum version"))?;
    if minimum.major == 0 || (minimum.major, minimum.minor) > (maximum.major, maximum.minor) {
        return Err(ProtocolError::Invalid("invalid version range"));
    }
    Ok((minimum.major, minimum.minor) <= (major, minor)
        && (major, minor) <= (maximum.major, maximum.minor))
}

fn validate_transfer_limit_offer(limits: Option<&v1::TransferLimits>) -> Result<(), ProtocolError> {
    let limits = limits.ok_or(ProtocolError::Invalid("missing transfer limits"))?;
    if limits.max_encoded_application_bytes == 0
        || limits.max_decoded_application_bytes == 0
        || limits.max_transaction_group_bytes == 0
        || limits.max_event_metadata_bytes == 0
        || limits.max_changes_per_group == 0
        || limits.max_grpc_message_bytes == 0
        || limits.max_in_flight_batches == 0
        || limits.max_open_streams == 0
        || limits.byte_credit == 0
    {
        return Err(ProtocolError::Invalid("zero transfer capacity"));
    }
    Ok(())
}

pub(crate) fn validate_transfer_limits(limits: &v1::TransferLimits) -> Result<(), ProtocolError> {
    validate_transfer_limit_offer(Some(limits))?;
    if limits.max_encoded_application_bytes as usize > MAX_ENCODED_APPLICATION_BYTES
        || limits.max_decoded_application_bytes as usize > MAX_DECODED_APPLICATION_BYTES
        || limits.max_transaction_group_bytes as usize > MAX_TRANSACTION_GROUP_BYTES
        || limits.max_event_metadata_bytes as usize > MAX_EVENT_METADATA_BYTES
        || limits.max_changes_per_group as usize > crate::MAX_CHANGES_PER_GROUP
        || limits.max_grpc_message_bytes as usize > MAX_GRPC_MESSAGE_BYTES
        || limits.max_in_flight_batches as usize > MAX_IN_FLIGHT_BATCHES_PER_PEER
        || limits.max_open_streams as usize > MAX_OPEN_STREAMS_PER_PEER
        || limits.byte_credit > MAX_IN_FLIGHT_BYTES_PER_PEER as u64
    {
        return Err(ProtocolError::LimitExceeded("local transfer limit"));
    }
    Ok(())
}

/// Constructs an explicit local v1 offer with standard wire ceilings and
/// caller-selected directional credits/concurrency.
///
/// # Errors
///
/// Returns [`ProtocolError::LimitExceeded`] when a selected capacity cannot fit
/// its wire field, or an admission/transfer error when it exceeds a v1 ceiling.
pub fn v1_transfer_limits(
    byte_credit: usize,
    max_in_flight_batches: usize,
    max_open_streams: usize,
) -> Result<v1::TransferLimits, ProtocolError> {
    let limits = v1::TransferLimits {
        max_encoded_application_bytes: wire_limit_u32(
            MAX_ENCODED_APPLICATION_BYTES,
            "encoded application bytes",
        )?,
        max_decoded_application_bytes: wire_limit_u32(
            MAX_DECODED_APPLICATION_BYTES,
            "decoded application bytes",
        )?,
        max_transaction_group_bytes: wire_limit_u32(
            MAX_TRANSACTION_GROUP_BYTES,
            "transaction group bytes",
        )?,
        max_event_metadata_bytes: wire_limit_u32(MAX_EVENT_METADATA_BYTES, "event metadata bytes")?,
        max_changes_per_group: wire_limit_u32(
            crate::MAX_CHANGES_PER_GROUP,
            "changes per transaction group",
        )?,
        max_grpc_message_bytes: wire_limit_u32(MAX_GRPC_MESSAGE_BYTES, "gRPC message bytes")?,
        max_in_flight_batches: u32::try_from(max_in_flight_batches)
            .map_err(|_| ProtocolError::LimitExceeded("in-flight batches"))?,
        max_open_streams: u32::try_from(max_open_streams)
            .map_err(|_| ProtocolError::LimitExceeded("open streams"))?,
        byte_credit: u64::try_from(byte_credit)
            .map_err(|_| ProtocolError::LimitExceeded("byte credit"))?,
    };
    validate_transfer_limits(&limits)?;
    Ok(limits)
}

fn wire_limit_u32(value: usize, label: &'static str) -> Result<u32, ProtocolError> {
    u32::try_from(value).map_err(|_| ProtocolError::LimitExceeded(label))
}

fn negotiate_limits(
    local: &v1::TransferLimits,
    peer: Option<&v1::TransferLimits>,
) -> Result<v1::TransferLimits, ProtocolError> {
    validate_transfer_limits(local)?;
    let peer = peer.ok_or(ProtocolError::Invalid("missing transfer limits"))?;
    validate_transfer_limit_offer(Some(peer))?;
    let min_u32 = |left: u32, right: u32| left.min(right);
    let max_grpc_message_bytes = min_u32(local.max_grpc_message_bytes, peer.max_grpc_message_bytes);
    let max_encoded_application_bytes = min_u32(
        local.max_encoded_application_bytes,
        peer.max_encoded_application_bytes,
    )
    .min(max_grpc_message_bytes.saturating_sub(4_096));
    if max_encoded_application_bytes == 0 {
        return Err(ProtocolError::Invalid(
            "gRPC limit cannot contain a v1 application envelope",
        ));
    }
    Ok(v1::TransferLimits {
        max_encoded_application_bytes,
        max_decoded_application_bytes: min_u32(
            local.max_decoded_application_bytes,
            peer.max_decoded_application_bytes,
        ),
        max_transaction_group_bytes: min_u32(
            local.max_transaction_group_bytes,
            peer.max_transaction_group_bytes,
        ),
        max_event_metadata_bytes: min_u32(
            local.max_event_metadata_bytes,
            peer.max_event_metadata_bytes,
        ),
        max_changes_per_group: min_u32(local.max_changes_per_group, peer.max_changes_per_group),
        max_grpc_message_bytes,
        max_in_flight_batches: min_u32(local.max_in_flight_batches, peer.max_in_flight_batches),
        max_open_streams: min_u32(local.max_open_streams, peer.max_open_streams),
        // Credit is directional. This welcome advertises local receive capacity;
        // the peer's offered credit is consumed by our sender independently.
        byte_credit: local.byte_credit.min(MAX_IN_FLIGHT_BYTES_PER_PEER as u64),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use bytes::Bytes;

    fn version_range(major: u32, minor: u32) -> v1::VersionRange {
        version_range_between(major, minor, major, minor)
    }

    fn version_range_between(
        minimum_major: u32,
        minimum_minor: u32,
        maximum_major: u32,
        maximum_minor: u32,
    ) -> v1::VersionRange {
        v1::VersionRange {
            minimum: Some(v1::ProtocolVersion {
                major: minimum_major,
                minor: minimum_minor,
            }),
            maximum: Some(v1::ProtocolVersion {
                major: maximum_major,
                minor: maximum_minor,
            }),
        }
    }

    fn identity(member_id: u8, incarnation: u8) -> v1::PeerIdentity {
        v1::PeerIdentity {
            tenant_id: Bytes::copy_from_slice(&[1; 16]),
            database_id: Bytes::copy_from_slice(&[2; 16]),
            shard_id: 3,
            member_id: Bytes::copy_from_slice(&[member_id; 16]),
            member_incarnation: Bytes::copy_from_slice(&[incarnation; 16]),
            membership_epoch: 4,
            configuration_id: Bytes::copy_from_slice(&[5; 32]),
        }
    }

    fn handshake(frontiers: Vec<v1::PeerFrontier>) -> (v1::Hello, NegotiationPolicy) {
        let local_identity = identity(6, 7);
        let peer_identity = identity(8, 9);
        let limits = v1_transfer_limits(1024 * 1024, 4, 2).expect("valid transfer limits");
        let hello = v1::Hello {
            local_identity: Some(peer_identity),
            expected_remote_member_id: local_identity.member_id.clone(),
            protocol_versions: Some(version_range(V1_PROTOCOL_MAJOR, V1_PROTOCOL_MINOR)),
            event_format_versions: Some(version_range(V1_EVENT_FORMAT, 0)),
            record_format_versions: Some(version_range(1, 0)),
            codecs: vec![
                PayloadCodec::Identity as i32,
                PayloadCodec::Lz4BlockV1 as i32,
            ],
            limits: Some(limits),
            frontiers,
            minimum_merge_policy_version: 1,
            maximum_merge_policy_version: 1,
        };
        let policy = NegotiationPolicy {
            local_identity,
            protocol_version: v1::ProtocolVersion {
                major: V1_PROTOCOL_MAJOR,
                minor: V1_PROTOCOL_MINOR,
            },
            event_format_version: V1_EVENT_FORMAT,
            record_format_versions: vec![1, 2],
            merge_policy_version: 1,
            codecs: vec![PayloadCodec::Identity, PayloadCodec::Lz4BlockV1],
            limits,
            frontiers: vec![frontier(Some(120), Some(110), Some(100))],
        };
        (hello, policy)
    }

    fn frontier(
        received_sequence: Option<u64>,
        durable_sequence: Option<u64>,
        applied_sequence: Option<u64>,
    ) -> v1::PeerFrontier {
        v1::PeerFrontier {
            origin: Some(v1::OriginIdentity {
                replica_id: Bytes::copy_from_slice(&[10; 16]),
                incarnation: Bytes::copy_from_slice(&[11; 16]),
            }),
            received_sequence,
            durable_sequence,
            applied_sequence,
        }
    }

    #[test]
    fn negotiation_rejects_applied_frontier_without_durable_prefix() {
        let offered = vec![frontier(None, None, Some(100))];
        let (hello, policy) = handshake(offered);

        assert!(matches!(
            negotiate_hello(&hello, &policy),
            Err(ProtocolError::Invalid("applied frontier exceeds durable"))
        ));
    }

    #[test]
    fn negotiation_allows_durable_prefix_without_received_prefix_and_reports_local_progress() {
        let offered = vec![frontier(None, Some(100), None)];
        let (hello, policy) = handshake(offered);

        let welcome = negotiate_hello(&hello, &policy).expect("durable prefix is valid");
        assert_eq!(welcome.frontiers, policy.frontiers);
    }

    #[test]
    fn negotiation_returns_the_validated_codec_intersection() {
        let (hello, mut policy) = handshake(Vec::new());
        policy.codecs = vec![PayloadCodec::Lz4BlockV1];

        let welcome = negotiate_hello(&hello, &policy).expect("LZ4 is common");
        assert_eq!(welcome.codecs, vec![PayloadCodec::Lz4BlockV1 as i32]);
    }

    #[test]
    fn negotiation_selects_v1_for_a_v1_only_peer() {
        let (hello, policy) = handshake(Vec::new());

        let welcome = negotiate_hello(&hello, &policy).expect("v1 is common");
        assert_eq!(welcome.record_format_version, 1);
    }

    #[test]
    fn negotiation_selects_v2_for_a_v2_only_peer() {
        let (mut hello, policy) = handshake(Vec::new());
        hello.record_format_versions = Some(version_range(2, 0));

        let welcome = negotiate_hello(&hello, &policy).expect("v2 is common");
        assert_eq!(welcome.record_format_version, 2);
    }

    #[test]
    fn negotiation_selects_the_highest_common_format_for_a_broad_peer_range() {
        let (mut hello, policy) = handshake(Vec::new());
        hello.record_format_versions = Some(version_range_between(1, 0, 2, 0));

        let welcome = negotiate_hello(&hello, &policy).expect("v1 and v2 are common");
        assert_eq!(welcome.record_format_version, 2);
    }

    #[test]
    fn negotiation_rejects_an_unknown_record_format_range() {
        let (mut hello, policy) = handshake(Vec::new());
        hello.record_format_versions = Some(version_range(3, 0));

        assert!(matches!(
            negotiate_hello(&hello, &policy),
            Err(ProtocolError::Invalid("no common record format"))
        ));
    }

    #[test]
    fn negotiation_rejects_duplicate_and_unknown_peer_codecs() {
        let (mut hello, policy) = handshake(Vec::new());
        hello.codecs = vec![PayloadCodec::Identity as i32, PayloadCodec::Identity as i32];
        assert!(matches!(
            negotiate_hello(&hello, &policy),
            Err(ProtocolError::Invalid("duplicate peer application codec"))
        ));

        hello.codecs = vec![i32::MAX];
        assert!(matches!(
            negotiate_hello(&hello, &policy),
            Err(ProtocolError::Invalid("unknown peer application codec"))
        ));

        hello.codecs = vec![
            PayloadCodec::Identity as i32,
            PayloadCodec::Lz4BlockV1 as i32,
            PayloadCodec::Identity as i32,
        ];
        assert!(matches!(
            negotiate_hello(&hello, &policy),
            Err(ProtocolError::Invalid("too many peer application codecs"))
        ));
    }
}
