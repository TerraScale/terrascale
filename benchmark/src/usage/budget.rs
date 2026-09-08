//! Signed regional write-budget allocations and their consumer-side checks.

#![allow(missing_docs)]

use aws_lc_rs::{
    digest::{SHA256, digest},
    signature::{ECDSA_P256_SHA256_ASN1, UnparsedPublicKey},
};
use serde::{Deserialize, Serialize};

/// Signature domain shared with the GRU write-budget producer.
pub const ALLOCATION_SIGNING_DOMAIN: &[u8] = b"terrascale.write-budget.pool-refill.v2\0";
/// Maximum signed allocation payload accepted from the control plane.
pub const MAX_ALLOCATION_BYTES: usize = 64 << 10;

/// A bounded regional pool refill. Local units are installed once; aggregate
/// units are the single Region-multiplied reservation at GRU.
#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub struct AllocationGrant {
    pub schema_version: u32,
    pub kind: String,
    pub refill_operation_id: String,
    pub pool_id: String,
    pub cycle_id: String,
    pub rate_nano_dollar: i64,
    pub organization_id: String,
    pub database_id: String,
    pub branch_id: String,
    pub holder_id: String,
    pub region: String,
    #[serde(rename = "selected_regions")]
    pub regions: Vec<String>,
    pub placement: String,
    pub local_units: i64,
    pub aggregate_units: i64,
    pub included_local_units: i64,
    pub paid_local_units: i64,
    pub included_aggregate_units: i64,
    pub paid_aggregate_units: i64,
    pub pool_sequence: u64,
    pub policy_epoch: u64,
    pub consent_epoch: u64,
    pub writer_epoch: u64,
    pub issued_at: String,
    pub expires_at: String,
}

/// Exact signed payload emitted by `writebudget.SignPoolRefill`.
#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub struct AllocationEnvelope {
    pub schema_version: u32,
    pub grant: AllocationGrant,
}

/// Exact signed delivery wrapper emitted by the producer.
#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub struct SignedAllocation {
    #[serde(with = "base64_bytes")]
    pub payload: Vec<u8>,
    #[serde(with = "base64_bytes")]
    pub signature: Vec<u8>,
    pub key_id: String,
}

mod base64_bytes {
    use base64::{Engine as _, engine::general_purpose::STANDARD};
    use serde::{Deserializer, Serializer, de::Error};
    pub fn serialize<S: Serializer>(bytes: &[u8], serializer: S) -> Result<S::Ok, S::Error> {
        serializer.serialize_str(&STANDARD.encode(bytes))
    }
    pub fn deserialize<'de, D: Deserializer<'de>>(deserializer: D) -> Result<Vec<u8>, D::Error> {
        STANDARD.decode(String::deserialize(deserializer)?).map_err(D::Error::custom)
    }
    use serde::Deserialize;
}

/// A verified allocation bound to the local regional writer.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct VerifiedAllocation {
    envelope: AllocationEnvelope,
    payload_digest: [u8; 32],
}

/// Allocation verification or consumption refusal.
#[derive(Clone, Copy, Debug, Eq, PartialEq, thiserror::Error)]
pub enum AllocationError {
    #[error("allocation encoding is invalid")]
    Encoding,
    #[error("allocation signature is invalid")]
    Signature,
    #[error("allocation binding is invalid")]
    Binding,
    #[error("allocation is expired or revoked")]
    Expired,
    #[error("allocation is exhausted")]
    Exhausted,
}

impl VerifiedAllocation {
    /// Verifies the exact producer bytes and all local identity bindings.
    ///
    /// The argument list is the frozen producer/consumer binding contract;
    /// keep it positional so callers cannot accidentally omit an identity.
    #[expect(clippy::too_many_arguments, reason = "frozen allocation verification contract")]
    pub fn verify(
        signed_bytes: &[u8],
        expected_key_id: &str,
        public_key: &[u8],
        local_region: &str,
        expected_placement: &str,
        expected_organization: &str,
        expected_database: &str,
        expected_holder: &str,
        expected_policy_epoch: u64,
        expected_consent_epoch: u64,
        expected_writer_epoch: u64,
        now_millis: u64,
    ) -> Result<Self, AllocationError> {
        if signed_bytes.len() > MAX_ALLOCATION_BYTES
            || expected_key_id.is_empty()
            || public_key.is_empty()
        {
            return Err(AllocationError::Encoding);
        }
        let signed: SignedAllocation =
            serde_json::from_slice(signed_bytes).map_err(|_| AllocationError::Encoding)?;
        let Some(key_id) = hex::decode(expected_key_id).ok() else {
            return Err(AllocationError::Signature);
        };
        if key_id.len() != 32
            || signed.key_id != expected_key_id
            || signed.signature.len() < 41
            || signed.payload.len() > MAX_ALLOCATION_BYTES
        {
            return Err(AllocationError::Signature);
        }
        if &signed.signature[..4] != b"TSCS"
            || signed.signature[4] != 1
            || signed.signature[5] != 1
            || signed.signature[6..38] != key_id
            || u16::from_be_bytes([signed.signature[38], signed.signature[39]]) as usize
                != signed.signature.len() - 40
        {
            return Err(AllocationError::Signature);
        }
        let mut message =
            Vec::with_capacity(ALLOCATION_SIGNING_DOMAIN.len() + signed.payload.len());
        message.extend_from_slice(ALLOCATION_SIGNING_DOMAIN);
        message.extend_from_slice(&signed.payload);
        let public_key = spki_point(public_key).ok_or(AllocationError::Signature)?;
        let configured_spki_digest = digest(&SHA256, &spki_der(public_key));
        if hex::encode(configured_spki_digest.as_ref()) != expected_key_id {
            return Err(AllocationError::Signature);
        }
        UnparsedPublicKey::new(&ECDSA_P256_SHA256_ASN1, public_key)
            .verify(&message, &signed.signature[40..])
            .map_err(|_| AllocationError::Signature)?;
        let envelope: AllocationEnvelope =
            serde_json::from_slice(&signed.payload).map_err(|_| AllocationError::Encoding)?;
        let grant = &envelope.grant;
        let regions = grant.regions.iter().collect::<std::collections::BTreeSet<_>>();
        let valid_times = parse_millis(&grant.issued_at).zip(parse_millis(&grant.expires_at));
        let Some((issued, expires)) = valid_times else { return Err(AllocationError::Binding) };
        if envelope.schema_version != 2
            || grant.schema_version != 2
            || grant.kind != "regional_write_pool_refill"
            || grant.cycle_id.is_empty()
            || grant.rate_nano_dollar < 0
            || grant.placement != expected_placement
            || grant.refill_operation_id.is_empty()
            || grant.pool_id.is_empty()
            || grant.organization_id != expected_organization
            || grant.database_id != expected_database
            || grant.holder_id != expected_holder
            || grant.local_units <= 0
            || grant.aggregate_units
                != grant
                    .local_units
                    .checked_mul(
                        i64::try_from(regions.len()).map_err(|_| AllocationError::Binding)?,
                    )
                    .ok_or(AllocationError::Binding)?
            || grant.aggregate_units <= 0
            || grant.included_local_units < 0
            || grant.paid_local_units < 0
            || grant.included_local_units.checked_add(grant.paid_local_units)
                != Some(grant.local_units)
            || grant.included_aggregate_units.checked_add(grant.paid_aggregate_units)
                != Some(grant.aggregate_units)
            || grant.paid_aggregate_units.checked_mul(grant.rate_nano_dollar).is_none()
            || grant.policy_epoch != expected_policy_epoch
            || grant.consent_epoch != expected_consent_epoch
            || grant.writer_epoch != expected_writer_epoch
            || issued == 0
            || expires <= now_millis
            || issued > expires
            || regions.len() != grant.regions.len()
            || !grant.regions.iter().any(|region| region == local_region)
        {
            return Err(if expires <= now_millis {
                AllocationError::Expired
            } else {
                AllocationError::Binding
            });
        }
        let payload_digest = *blake3::hash(&signed.payload).as_bytes();
        Ok(Self { envelope, payload_digest })
    }

    #[must_use]
    pub fn grant(&self) -> &AllocationGrant {
        &self.envelope.grant
    }
    #[must_use]
    pub fn cycle_id(&self) -> &str {
        &self.envelope.grant.cycle_id
    }
    #[must_use]
    pub fn placement(&self) -> &str {
        &self.envelope.grant.placement
    }
    #[must_use]
    pub fn payload_digest(&self) -> &[u8; 32] {
        &self.payload_digest
    }
    #[must_use]
    pub fn local_units(&self) -> i64 {
        self.envelope.grant.local_units
    }
    #[must_use]
    pub fn valid_at(&self, now_millis: u64) -> bool {
        parse_millis(&self.envelope.grant.expires_at).is_some_and(|expires| expires > now_millis)
    }
}

fn spki_der(point: &[u8]) -> Vec<u8> {
    let mut der = vec![
        0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01, 0x06, 0x08,
        0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00,
    ];
    der.extend_from_slice(point);
    der
}

fn parse_millis(value: &str) -> Option<u64> {
    // Go's time.Time JSON form is RFC3339Nano. Keep this parser local to the
    // wire contract so the engine does not acquire a second date library.
    let (date, rest) = value.split_once('T')?;
    let mut date_parts = date.split('-');
    let year: i64 = date_parts.next()?.parse().ok()?;
    let month: i64 = date_parts.next()?.parse().ok()?;
    let day: i64 = date_parts.next()?.parse().ok()?;
    if date_parts.next().is_some() || !(1..=12).contains(&month) || day == 0 || day > 31 {
        return None;
    }
    let (clock, zone): (&str, &str) = if let Some(clock) = rest.strip_suffix('Z') {
        (clock, "Z")
    } else {
        let split = rest.char_indices().rev().find(|(_, c)| *c == '+' || *c == '-')?.0;
        (&rest[..split], &rest[split..])
    };
    let mut clock_parts = clock.split(':');
    let hour: i64 = clock_parts.next()?.parse().ok()?;
    let minute: i64 = clock_parts.next()?.parse().ok()?;
    let second_fraction = clock_parts.next()?;
    let (second, fraction) = second_fraction.split_once('.').unwrap_or((second_fraction, ""));
    let second: i64 = second.parse().ok()?;
    if hour > 23 || minute > 59 || second > 59 {
        return None;
    }
    let offset_minutes = if zone == "Z" {
        0
    } else {
        let sign = if zone.starts_with('+') {
            1
        } else if zone.starts_with('-') {
            -1
        } else {
            return None;
        };
        let mut p = zone[1..].split(':');
        sign * (p
            .next()?
            .parse::<i64>()
            .ok()?
            .checked_mul(60)?
            .checked_add(p.next()?.parse().ok()?)?)
    };
    let days = days_from_civil(year, month, day)?;
    let millis = days
        .checked_mul(86_400_000)?
        .checked_add((hour * 3600 + minute * 60 + second) * 1000)?
        .checked_add(fraction_millis(fraction)?)?
        .checked_sub(offset_minutes * 60 * 1000)?;
    u64::try_from(millis).ok()
}

fn fraction_millis(value: &str) -> Option<i64> {
    if value.is_empty() {
        return Some(0);
    }
    if value.len() > 9 || !value.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    let mut n: i64 = value.parse().ok()?;
    for _ in value.len()..9 {
        n *= 10;
    }
    Some(n / 1_000_000)
}

fn spki_point(key: &[u8]) -> Option<&[u8]> {
    const PREFIX: &[u8] = &[
        0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01, 0x06, 0x08,
        0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00,
    ];
    if key.len() == PREFIX.len() + 65 && key.starts_with(PREFIX) && key[PREFIX.len()] == 4 {
        return Some(&key[PREFIX.len()..]);
    }
    None
}

// Howard Hinnant's proleptic Gregorian conversion, with Unix epoch origin.
fn days_from_civil(year: i64, month: i64, day: i64) -> Option<i64> {
    let y = year.checked_sub(i64::from(month <= 2))?;
    let era = if y >= 0 { y } else { y - 399 }.div_euclid(400);
    let yoe = y - era * 400;
    let mp = month + if month > 2 { -3 } else { 9 };
    let doy = (153 * mp + 2) / 5 + day - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era.checked_mul(146_097)?.checked_add(doe)?.checked_sub(719_468)
}

#[cfg(test)]
mod tests {
    use super::*;
    use aws_lc_rs::{
        rand::SystemRandom,
        signature::{ECDSA_P256_SHA256_ASN1_SIGNING, EcdsaKeyPair, KeyPair},
    };
    use base64::Engine as _;

    fn signed(holder: &str) -> (Vec<u8>, Vec<u8>, String) {
        let rng = SystemRandom::new();
        let pkcs8 = EcdsaKeyPair::generate_pkcs8(&ECDSA_P256_SHA256_ASN1_SIGNING, &rng).unwrap();
        let key =
            EcdsaKeyPair::from_pkcs8(&ECDSA_P256_SHA256_ASN1_SIGNING, pkcs8.as_ref()).unwrap();
        let mut public_spki = vec![
            0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01, 0x06,
            0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00,
        ];
        public_spki.extend_from_slice(key.public_key().as_ref());
        let key_id = hex::encode(digest(&SHA256, &public_spki).as_ref());
        let grant = AllocationGrant {
            schema_version: 2,
            kind: "regional_write_pool_refill".into(),
            refill_operation_id: "refill-1".into(),
            pool_id: "pool-1".into(),
            cycle_id: "cycle-1".into(),
            rate_nano_dollar: 1,
            organization_id: "org-1".into(),
            database_id: "db-1".into(),
            branch_id: "main".into(),
            holder_id: holder.into(),
            region: "gru".into(),
            regions: vec!["gru".into(), "sfo".into(), "fra".into()],
            placement: "placement-1".into(),
            local_units: 2,
            aggregate_units: 6,
            included_local_units: 0,
            paid_local_units: 2,
            included_aggregate_units: 0,
            paid_aggregate_units: 6,
            pool_sequence: 1,
            policy_epoch: 4,
            consent_epoch: 7,
            writer_epoch: 9,
            issued_at: "2026-01-01T00:00:00Z".into(),
            expires_at: "2099-01-01T00:00:00Z".into(),
        };
        let envelope = AllocationEnvelope {
            schema_version: 2,
            grant,
        };
        let payload = serde_json::to_vec(&envelope).unwrap();
        let mut message = ALLOCATION_SIGNING_DOMAIN.to_vec();
        message.extend_from_slice(&payload);
        let signature = key.sign(&rng, &message).unwrap();
        let mut wrapped = b"TSCS".to_vec();
        wrapped.extend_from_slice(&[1, 1]);
        wrapped.extend_from_slice(digest(&SHA256, &public_spki).as_ref());
        let signature_length = u16::try_from(signature.as_ref().len()).unwrap();
        wrapped.extend_from_slice(&signature_length.to_be_bytes());
        wrapped.extend_from_slice(signature.as_ref());
        let wire = serde_json::to_vec(&SignedAllocation {
            payload,
            signature: wrapped,
            key_id: key_id.clone(),
        })
        .unwrap();
        (wire, public_spki, key_id)
    }

    #[test]
    fn signed_allocation_rejects_wrong_holder_and_accepts_exact_binding() {
        let (wire, public, key_id) = signed("holder-1");
        assert!(
            VerifiedAllocation::verify(
                &wire,
                &key_id,
                &public,
                "gru",
                "placement-1",
                "org-1",
                "db-1",
                "holder-2",
                4,
                7,
                9,
                1_800_000_000_000
            )
            .is_err()
        );
        assert!(
            VerifiedAllocation::verify(
                &wire,
                &key_id,
                &public,
                "gru",
                "placement-1",
                "org-1",
                "db-1",
                "holder-1",
                4,
                7,
                9,
                1_800_000_000_000
            )
            .is_ok()
        );
    }

    #[test]
    fn signed_allocation_rejects_expired_at_verification() {
        let (wire, public, key_id) = signed("holder-1");
        assert_eq!(
            VerifiedAllocation::verify(
                &wire,
                &key_id,
                &public,
                "gru",
                "placement-1",
                "org-1",
                "db-1",
                "holder-1",
                4,
                7,
                9,
                4_100_000_000_000
            )
            .unwrap_err(),
            AllocationError::Expired
        );
    }

    #[test]
    fn go_signed_allocation_vector_verifies() {
        let vector: serde_json::Value = serde_json::from_slice(include_bytes!(
            "../../testdata/allocation-canonical-vectors.json"
        ))
        .unwrap();
        let public = base64::engine::general_purpose::STANDARD
            .decode(vector["publicSPKI"].as_str().unwrap())
            .unwrap();
        let key_id = vector["keyID"].as_str().unwrap();
        for entry in vector["vectors"].as_array().unwrap() {
            let payload = base64::engine::general_purpose::STANDARD
                .decode(entry["payload"].as_str().unwrap())
                .unwrap();
            let signature = base64::engine::general_purpose::STANDARD
                .decode(entry["signature"].as_str().unwrap())
                .unwrap();
            let envelope: AllocationEnvelope = serde_json::from_slice(&payload).unwrap();
            let wire =
                serde_json::to_vec(&SignedAllocation { payload, signature, key_id: key_id.into() })
                    .unwrap();
            let grant = &envelope.grant;
            let verified = VerifiedAllocation::verify(
                &wire,
                key_id,
                &public,
                "gru",
                &envelope.placement,
                &grant.organization_id,
                &grant.database_id,
                &grant.holder_id,
                grant.policy_epoch,
                grant.consent_epoch,
                grant.writer_epoch,
                0,
            )
            .unwrap();
            assert_eq!(verified.grant().units, entry["units"].as_i64().unwrap());
        }
    }
}
