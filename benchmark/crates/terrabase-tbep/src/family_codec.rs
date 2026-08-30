//! Canonical protobuf payloads for the advertised data-family operations.
//!
//! The gRPC envelope deliberately keeps `request` and `response` opaque.  A
//! family payload is nevertheless a closed, versioned protobuf oneof so a
//! receiver can reject unknown operations, malformed bytes, and an outer
//! family/tag mismatch before dispatching to a host.

use prost::Message;

use crate::{proto::terrabase::v1 as pb, registry};

/// The v1 request payload codec identifier.
pub const REQUEST_CODEC: &str = "terrabase.family-request/1";
/// The v1 response payload codec identifier.
pub const RESPONSE_CODEC: &str = "terrabase.family-response/1";
/// The maximum encoded family payload accepted by this codec.
pub const MAX_PAYLOAD_BYTES: usize = registry::MAX_FRAME_BYTES;

/// A malformed or semantically unsupported family payload.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum FamilyCodecError {
    /// The encoded payload exceeds [`MAX_PAYLOAD_BYTES`].
    PayloadTooLarge {
        /// Encoded byte count presented by the caller.
        actual: usize,
        /// Maximum encoded byte count accepted by the codec.
        bound: usize,
    },
    /// Prost could not decode the protobuf bytes.
    Malformed {
        /// Bounded decoder diagnostic.
        detail: String,
    },
    /// The message contained no operation/result oneof arm.
    MissingVariant {
        /// Static protobuf payload type name.
        payload: &'static str,
    },
    /// Decoding and canonical re-encoding did not preserve the bytes.
    NonCanonical,
    /// The payload's oneof arm belongs to a different advertised family.
    FamilyMismatch {
        /// Family advertised by the outer request.
        expected: String,
        /// Family encoded by the payload variant.
        actual: String,
    },
    /// The outer codec identifier does not match this payload family.
    CodecMismatch {
        /// Static codec identifier required by this operation.
        expected: &'static str,
        /// Codec identifier presented by the outer request.
        actual: String,
    },
}

impl std::fmt::Display for FamilyCodecError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::PayloadTooLarge { actual, bound } => {
                write!(f, "family payload is {actual} bytes; maximum is {bound}")
            }
            Self::Malformed { detail } => write!(f, "malformed family protobuf: {detail}"),
            Self::MissingVariant { payload } => write!(f, "{payload} has no oneof variant"),
            Self::NonCanonical => f.write_str("family protobuf is not canonical"),
            Self::FamilyMismatch { expected, actual } => {
                write!(f, "family payload is for {actual}, outer family is {expected}")
            }
            Self::CodecMismatch { expected, actual } => {
                write!(f, "family codec is {actual}, expected {expected}")
            }
        }
    }
}

impl std::error::Error for FamilyCodecError {}

fn bounded_encode<M: Message>(
    message: &M,
    payload: &'static str,
) -> Result<Vec<u8>, FamilyCodecError> {
    if message.encoded_len() > MAX_PAYLOAD_BYTES {
        return Err(FamilyCodecError::PayloadTooLarge {
            actual: message.encoded_len(),
            bound: MAX_PAYLOAD_BYTES,
        });
    }
    if message.encoded_len() == 0 {
        return Err(FamilyCodecError::MissingVariant { payload });
    }
    Ok(message.encode_to_vec())
}

fn bounded_decode<M: Message + Default>(
    bytes: &[u8],
    payload: &'static str,
) -> Result<M, FamilyCodecError> {
    if bytes.len() > MAX_PAYLOAD_BYTES {
        return Err(FamilyCodecError::PayloadTooLarge {
            actual: bytes.len(),
            bound: MAX_PAYLOAD_BYTES,
        });
    }
    let message = M::decode(bytes)
        .map_err(|error| FamilyCodecError::Malformed { detail: error.to_string() })?;
    if message.encoded_len() == 0 {
        return Err(FamilyCodecError::MissingVariant { payload });
    }
    if message.encode_to_vec() != bytes {
        return Err(FamilyCodecError::NonCanonical);
    }
    Ok(message)
}

/// Encodes a canonical v1 family request payload.
pub fn encode_request(payload: &pb::FamilyRequestPayload) -> Result<Vec<u8>, FamilyCodecError> {
    if payload.operation.is_none() {
        return Err(FamilyCodecError::MissingVariant { payload: "FamilyRequestPayload" });
    }
    bounded_encode(payload, "FamilyRequestPayload")
}

/// Decodes a canonical v1 family request payload.
pub fn decode_request(bytes: &[u8]) -> Result<pb::FamilyRequestPayload, FamilyCodecError> {
    let payload = bounded_decode::<pb::FamilyRequestPayload>(bytes, "FamilyRequestPayload")?;
    if payload.operation.is_none() {
        return Err(FamilyCodecError::MissingVariant { payload: "FamilyRequestPayload" });
    }
    Ok(payload)
}

/// Encodes a canonical v1 family response payload.
pub fn encode_response(payload: &pb::FamilyResponsePayload) -> Result<Vec<u8>, FamilyCodecError> {
    if payload.result.is_none() {
        return Err(FamilyCodecError::MissingVariant { payload: "FamilyResponsePayload" });
    }
    bounded_encode(payload, "FamilyResponsePayload")
}

/// Decodes a canonical v1 family response payload.
pub fn decode_response(bytes: &[u8]) -> Result<pb::FamilyResponsePayload, FamilyCodecError> {
    let payload = bounded_decode::<pb::FamilyResponsePayload>(bytes, "FamilyResponsePayload")?;
    if payload.result.is_none() {
        return Err(FamilyCodecError::MissingVariant { payload: "FamilyResponsePayload" });
    }
    Ok(payload)
}

/// Returns the advertised family selected by a request payload.
pub fn request_family(
    payload: &pb::FamilyRequestPayload,
) -> Result<&'static str, FamilyCodecError> {
    use pb::family_request_payload::Operation;
    match payload.operation.as_ref() {
        Some(
            Operation::AttachmentRead(_)
            | Operation::AttachmentWrite(_)
            | Operation::AttachmentContentRead(_)
            | Operation::AttachmentContentWrite(_)
            | Operation::AttachmentContentDelete(_),
        ) => Ok("attachment"),
        Some(Operation::CounterRead(_) | Operation::CounterWrite(_)) => Ok("counter"),
        Some(Operation::TimeSeriesRead(_) | Operation::TimeSeriesWrite(_)) => Ok("time_series"),
        Some(Operation::RevisionRead(_) | Operation::RevisionWrite(_)) => Ok("revisions"),
        Some(Operation::CompareExchangeRead(_) | Operation::CompareExchangeWrite(_)) => {
            Ok("compare_exchange")
        }
        Some(Operation::IdentityRead(_) | Operation::IdentityWrite(_)) => Ok("identities"),
        Some(Operation::LifecycleRead(_) | Operation::LifecycleWrite(_)) => {
            Ok("expiration_refresh")
        }
        Some(Operation::ArchivalRead(_) | Operation::ArchivalWrite(_)) => Ok("archival"),
        None => Err(FamilyCodecError::MissingVariant { payload: "FamilyRequestPayload" }),
    }
}

/// Returns whether a request payload selects a write operation.
pub fn request_is_write(payload: &pb::FamilyRequestPayload) -> Result<bool, FamilyCodecError> {
    use pb::family_request_payload::Operation;
    Ok(match payload.operation.as_ref() {
        Some(
            Operation::AttachmentWrite(_)
            | Operation::CounterWrite(_)
            | Operation::TimeSeriesWrite(_)
            | Operation::RevisionWrite(_)
            | Operation::CompareExchangeWrite(_)
            | Operation::IdentityWrite(_)
            | Operation::LifecycleWrite(_)
            | Operation::ArchivalWrite(_)
            | Operation::AttachmentContentWrite(_)
            | Operation::AttachmentContentDelete(_),
        ) => true,
        Some(_) => false,
        None => return Err(FamilyCodecError::MissingVariant { payload: "FamilyRequestPayload" }),
    })
}

/// Returns the family selected by a response payload. Write receipts have no
/// family tag of their own and therefore return `None`.
pub fn response_family(
    payload: &pb::FamilyResponsePayload,
) -> Result<Option<&'static str>, FamilyCodecError> {
    use pb::family_response_payload::Result;
    match payload.result.as_ref() {
        Some(Result::Attachment(_) | Result::AttachmentContent(_)) => Ok(Some("attachment")),
        Some(Result::Counter(_)) => Ok(Some("counter")),
        Some(Result::TimeSeries(_)) => Ok(Some("time_series")),
        Some(Result::Revisions(_)) => Ok(Some("revisions")),
        Some(Result::CompareExchange(_)) => Ok(Some("compare_exchange")),
        Some(Result::Identities(_)) => Ok(Some("identities")),
        Some(Result::ExpirationRefresh(_)) => Ok(Some("expiration_refresh")),
        Some(Result::Archival(_)) => Ok(Some("archival")),
        Some(Result::Write(_)) => Ok(None),
        None => Err(FamilyCodecError::MissingVariant { payload: "FamilyResponsePayload" }),
    }
}

/// Validates a decoded request against the outer family and request codec.
pub fn validate_request_family(
    family: &str,
    request_codec: &str,
    payload: &pb::FamilyRequestPayload,
) -> Result<(), FamilyCodecError> {
    if request_codec != REQUEST_CODEC {
        return Err(FamilyCodecError::CodecMismatch {
            expected: REQUEST_CODEC,
            actual: request_codec.to_owned(),
        });
    }
    let actual = request_family(payload)?;
    if actual != family {
        return Err(FamilyCodecError::FamilyMismatch {
            expected: family.to_owned(),
            actual: actual.to_owned(),
        });
    }
    Ok(())
}

/// Validates a decoded response against the outer family and response codec.
pub fn validate_response_family(
    family: &str,
    response_codec: &str,
    payload: &pb::FamilyResponsePayload,
) -> Result<(), FamilyCodecError> {
    if response_codec != RESPONSE_CODEC {
        return Err(FamilyCodecError::CodecMismatch {
            expected: RESPONSE_CODEC,
            actual: response_codec.to_owned(),
        });
    }
    if let Some(actual) = response_family(payload)?
        && actual != family
    {
        return Err(FamilyCodecError::FamilyMismatch {
            expected: family.to_owned(),
            actual: actual.to_owned(),
        });
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn minimal_request_golden_and_round_trip() {
        let payload = pb::FamilyRequestPayload {
            operation: Some(pb::family_request_payload::Operation::AttachmentRead(
                pb::FamilyAttachmentRead { document: "doc".into() },
            )),
        };
        let bytes = encode_request(&payload).expect("encode");
        assert_eq!(bytes, vec![0x0a, 0x05, 0x0a, 0x03, b'd', b'o', b'c']);
        assert_eq!(decode_request(&bytes).expect("decode"), payload);
        assert_eq!(request_family(&payload), Ok("attachment"));
    }

    #[test]
    fn malformed_unknown_and_trailing_bytes_fail_closed() {
        assert!(matches!(decode_request(&[0x0a]), Err(FamilyCodecError::Malformed { .. })));
        assert!(matches!(
            decode_request(&[0x78, 0x01]),
            Err(FamilyCodecError::MissingVariant { .. })
        ));
        let payload = pb::FamilyRequestPayload {
            operation: Some(pb::family_request_payload::Operation::CounterRead(
                pb::FamilyCounterRead { document: "d".into(), names: Vec::new() },
            )),
        };
        let mut bytes = encode_request(&payload).expect("encode");
        bytes.push(0);
        assert!(matches!(decode_request(&bytes), Err(FamilyCodecError::Malformed { .. })));
        assert!(matches!(
            encode_request(&pb::FamilyRequestPayload { operation: None }),
            Err(FamilyCodecError::MissingVariant { .. })
        ));
    }

    #[test]
    fn family_and_codec_validation_is_closed() {
        let payload = pb::FamilyRequestPayload {
            operation: Some(pb::family_request_payload::Operation::ArchivalRead(
                pb::FamilyArchivalRead { document: "d".into() },
            )),
        };
        assert!(validate_request_family("archival", REQUEST_CODEC, &payload).is_ok());
        assert!(matches!(
            validate_request_family("counter", REQUEST_CODEC, &payload),
            Err(FamilyCodecError::FamilyMismatch { .. })
        ));
        assert!(matches!(
            validate_request_family("archival", "other", &payload),
            Err(FamilyCodecError::CodecMismatch { .. })
        ));
    }

    #[test]
    fn every_advertised_family_has_request_and_response_tags() {
        let requests = [
            "attachment",
            "counter",
            "time_series",
            "revisions",
            "compare_exchange",
            "identities",
            "expiration_refresh",
            "archival",
        ];
        for family in requests {
            let payload = match family {
                "attachment" => pb::FamilyRequestPayload {
                    operation: Some(pb::family_request_payload::Operation::AttachmentRead(
                        pb::FamilyAttachmentRead::default(),
                    )),
                },
                "counter" => pb::FamilyRequestPayload {
                    operation: Some(pb::family_request_payload::Operation::CounterRead(
                        pb::FamilyCounterRead::default(),
                    )),
                },
                "time_series" => pb::FamilyRequestPayload {
                    operation: Some(pb::family_request_payload::Operation::TimeSeriesRead(
                        pb::FamilyTimeSeriesRead::default(),
                    )),
                },
                "revisions" => pb::FamilyRequestPayload {
                    operation: Some(pb::family_request_payload::Operation::RevisionRead(
                        pb::FamilyRevisionRead::default(),
                    )),
                },
                "compare_exchange" => pb::FamilyRequestPayload {
                    operation: Some(pb::family_request_payload::Operation::CompareExchangeRead(
                        pb::FamilyCompareExchangeRead::default(),
                    )),
                },
                "identities" => pb::FamilyRequestPayload {
                    operation: Some(pb::family_request_payload::Operation::IdentityRead(
                        pb::FamilyIdentityRead::default(),
                    )),
                },
                "expiration_refresh" => pb::FamilyRequestPayload {
                    operation: Some(pb::family_request_payload::Operation::LifecycleRead(
                        pb::FamilyLifecycleRead::default(),
                    )),
                },
                "archival" => pb::FamilyRequestPayload {
                    operation: Some(pb::family_request_payload::Operation::ArchivalRead(
                        pb::FamilyArchivalRead::default(),
                    )),
                },
                _ => unreachable!(),
            };
            assert_eq!(request_family(&payload), Ok(family));
        }
    }
}
