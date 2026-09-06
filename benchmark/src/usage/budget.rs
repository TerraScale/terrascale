//! Signed regional write-budget allocations and their consumer-side checks.

#![allow(missing_docs)]

use aws_lc_rs::signature::{ECDSA_P256_SHA256_ASN1, UnparsedPublicKey};
use serde::{Deserialize, Serialize};

/// Signature domain shared with the GRU write-budget producer.
pub const ALLOCATION_SIGNING_DOMAIN: &[u8] = b"terrascale.write-budget.allocation.v1\0";
/// Maximum signed allocation payload accepted from the control plane.
pub const MAX_ALLOCATION_BYTES: usize = 64 << 10;

/// The producer's grant object. `units` is already multiplied by placement
/// Regions; a regional consumer charges `requested_units` exactly once.
#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub struct AllocationGrant {
    pub operation_id: String,
    pub organization_id: String,
    pub database_id: String,
    pub branch_id: String,
    pub holder_id: String,
    pub requested_units: i64,
    pub regions: Vec<String>,
    pub units: i64,
    pub cost_minor: i64,
    pub cost_nano_dollar: i64,
    pub policy_epoch: u64,
    pub consent_epoch: u64,
    pub issued_at: String,
    pub expires_at: String,
    #[serde(default)]
    pub revoked: bool,
    #[serde(default)]
    pub fenced: bool,
}

/// Exact signed payload emitted by `writebudget.SignAllocation`.
#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub struct AllocationEnvelope {
    pub schema_version: u32,
    pub grant: AllocationGrant,
    pub cycle_id: String,
    pub rate_nano_dollar: i64,
    pub placement: String,
}

/// Exact signed delivery wrapper emitted by the producer.
#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub struct SignedAllocation {
    pub payload: Vec<u8>,
    pub signature: Vec<u8>,
    pub key_id: String,
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
    pub fn verify(
        signed_bytes: &[u8],
        expected_key_id: &str,
        public_key: &[u8],
        local_region: &str,
        expected_placement: &str,
        expected_database: &str,
        expected_policy_epoch: u64,
        expected_consent_epoch: u64,
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
        let Some(key_id) = hex::decode(expected_key_id).ok() else { return Err(AllocationError::Signature) };
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
        { return Err(AllocationError::Signature); }
        let mut message =
            Vec::with_capacity(ALLOCATION_SIGNING_DOMAIN.len() + signed.payload.len());
        message.extend_from_slice(ALLOCATION_SIGNING_DOMAIN);
        message.extend_from_slice(&signed.payload);
        let public_key = spki_point(public_key).ok_or(AllocationError::Signature)?;
        UnparsedPublicKey::new(&ECDSA_P256_SHA256_ASN1, public_key)
            .verify(&message, &signed.signature)
            .map_err(|_| AllocationError::Signature)?;
        let envelope: AllocationEnvelope =
            serde_json::from_slice(&signed.payload).map_err(|_| AllocationError::Encoding)?;
        let grant = &envelope.grant;
        let regions = grant.regions.iter().collect::<std::collections::BTreeSet<_>>();
        let valid_times = parse_millis(&grant.issued_at).zip(parse_millis(&grant.expires_at));
        let Some((issued, expires)) = valid_times else { return Err(AllocationError::Binding) };
        if envelope.schema_version != 1
            || envelope.cycle_id.is_empty()
            || envelope.rate_nano_dollar < 0
            || envelope.placement != expected_placement
            || grant.operation_id.is_empty()
            || grant.organization_id.is_empty()
            || grant.database_id != expected_database
            || grant.holder_id.is_empty()
            || grant.requested_units <= 0
            || grant.units
                != grant
                    .requested_units
                    .checked_mul(
                        i64::try_from(regions.len()).map_err(|_| AllocationError::Binding)?,
                    )
                    .ok_or(AllocationError::Binding)?
            || grant.units <= 0
            || grant.cost_minor < 0
            || grant.cost_nano_dollar
                != grant
                    .units
                    .checked_mul(envelope.rate_nano_dollar)
                    .ok_or(AllocationError::Binding)?
            || grant.policy_epoch != expected_policy_epoch
            || grant.consent_epoch != expected_consent_epoch
            || grant.revoked
            || grant.fenced
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

    pub fn grant(&self) -> &AllocationGrant {
        &self.envelope.grant
    }
    pub fn cycle_id(&self) -> &str {
        &self.envelope.cycle_id
    }
    pub fn placement(&self) -> &str {
        &self.envelope.placement
    }
    pub fn payload_digest(&self) -> &[u8; 32] {
        &self.payload_digest
    }
    pub fn local_units(&self) -> i64 {
        self.envelope.grant.requested_units
    }
    pub fn valid_at(&self, now_millis: u64) -> bool {
        parse_millis(&self.envelope.grant.expires_at).is_some_and(|expires| expires > now_millis)
    }
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
    let (second, fraction) = second_fraction.split_once('.').map_or((second_fraction, ""), |v| v);
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
    if key.len() == 65 && key[0] == 4 { return Some(key); }
    let bit = key.iter().position(|byte| *byte == 0x03)?;
    let length = *key.get(bit + 1)? as usize;
    let body = key.get(bit + 2..bit + 2 + length)?;
    (body.first() == Some(&0) && body.get(1) == Some(&4) && body.len() == 66).then_some(&body[1..])
}

// Howard Hinnant's proleptic Gregorian conversion, with Unix epoch origin.
fn days_from_civil(year: i64, month: i64, day: i64) -> Option<i64> {
    let y = year.checked_sub(i64::from(month <= 2))?;
    let era = if y >= 0 { y } else { y - 399 }.div_euclid(400);
    let yoe = y - era * 400;
    let mp = month + if month > 2 { -3 } else { 9 };
    let doy = (153 * mp + 2) / 5 + day - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era.checked_mul(146097)?.checked_add(doe)?.checked_sub(719468)
}
