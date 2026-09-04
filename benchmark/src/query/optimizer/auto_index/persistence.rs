//! Versioned, checksummed, bounded lifecycle journal records and replay.

use std::collections::{BTreeMap, VecDeque};

use super::lifecycle::{
    AuditView, LifecycleError, LifecycleEvent, LifecycleIdentity, LifecycleOperation,
    LifecycleState,
};
use super::{AutoIndexPhase, Candidate};

pub(crate) const MAX_NAME_BYTES: usize = 128;
pub(crate) const MAX_RECORD_BYTES: usize = 16 * 1024;
pub(crate) const MAX_JOURNAL_BYTES: usize = 4 * 1024 * 1024;
pub(crate) const MAX_JOURNAL_RECORDS: usize = 16_384;
const MAGIC: [u8; 4] = *b"AIXJ";
const VERSION: u8 = 1;
const HEADER_BYTES: usize = 4 + 1 + 4 + 32;

/// A persistence record for one candidate lifecycle update.
#[derive(Clone, Debug)]
pub(crate) struct JournalRecord {
    pub(crate) candidate: Option<Candidate>,
    pub(crate) identity: LifecycleIdentity,
    pub(crate) operation: LifecycleOperation,
    pub(crate) from: AutoIndexPhase,
    pub(crate) phase: AutoIndexPhase,
    pub(crate) watermark: Option<super::build::BuildWatermark>,
    pub(crate) seq: u64,
    pub(crate) candidate_digest: [u8; 32],
}

impl JournalRecord {
    pub(crate) fn new(
        identity: LifecycleIdentity,
        operation: LifecycleOperation,
        from: AutoIndexPhase,
        to: AutoIndexPhase,
        watermark: Option<super::build::BuildWatermark>,
        candidate: Option<Candidate>,
    ) -> Result<Self, JournalError> {
        if identity.sequence() == 0 {
            return Err(JournalError::InvalidRecord);
        }
        let candidate_digest = candidate.as_ref().map_or([0; 32], digest_candidate);
        Ok(Self {
            seq: identity.sequence(),
            candidate,
            identity,
            operation,
            from,
            phase: to,
            watermark,
            candidate_digest,
        })
    }

    pub(crate) fn encode(&self) -> Result<Vec<u8>, JournalError> {
        if self.seq != self.identity.sequence() || self.seq == 0 {
            return Err(JournalError::InvalidRecord);
        }
        let mut payload = Vec::with_capacity(128);
        payload.push(self.operation.code());
        payload.push(phase_code(self.from));
        payload.push(phase_code(self.phase));
        payload.push(u8::from(self.watermark.is_some()));
        payload.extend_from_slice(&self.identity.generation().to_le_bytes());
        payload.extend_from_slice(&self.identity.schema().to_le_bytes());
        payload.extend_from_slice(&self.identity.sequence().to_le_bytes());
        put_name(&mut payload, self.identity.collection())?;
        put_name(&mut payload, self.identity.index())?;
        payload.extend_from_slice(&self.candidate_digest);
        if let Some(watermark) = self.watermark {
            payload.extend_from_slice(&watermark.seq().to_le_bytes());
        }
        if payload.len() > MAX_RECORD_BYTES {
            return Err(JournalError::RecordTooLarge);
        }
        let checksum = *blake3::hash(&payload).as_bytes();
        let payload_len = u32::try_from(payload.len()).map_err(|_| JournalError::RecordTooLarge)?;
        let mut frame = Vec::with_capacity(HEADER_BYTES + payload.len());
        frame.extend_from_slice(&MAGIC);
        frame.push(VERSION);
        frame.extend_from_slice(&payload_len.to_le_bytes());
        frame.extend_from_slice(&checksum);
        frame.extend_from_slice(&payload);
        Ok(frame)
    }

    fn decode(frame: &[u8]) -> Result<DecodedRecord, JournalError> {
        if frame.len() < HEADER_BYTES {
            return Err(JournalError::Truncated);
        }
        if frame[..4] != MAGIC || frame[4] != VERSION {
            return Err(JournalError::Corrupt);
        }
        let len =
            u32::from_le_bytes(frame[5..9].try_into().map_err(|_| JournalError::Corrupt)?) as usize;
        if len > MAX_RECORD_BYTES {
            return Err(JournalError::RecordTooLarge);
        }
        if frame.len() != HEADER_BYTES + len {
            return Err(JournalError::Truncated);
        }
        let expected = &frame[9..41];
        let payload = &frame[HEADER_BYTES..];
        if blake3::hash(payload).as_bytes() != expected {
            return Err(JournalError::Corrupt);
        }
        let mut cursor = Cursor::new(payload);
        let operation =
            LifecycleOperation::from_code(cursor.byte()?).ok_or(JournalError::InvalidRecord)?;
        let from = phase_from_code(cursor.byte()?).ok_or(JournalError::InvalidRecord)?;
        let to = phase_from_code(cursor.byte()?).ok_or(JournalError::InvalidRecord)?;
        let has_watermark = cursor.byte()?;
        if has_watermark > 1 {
            return Err(JournalError::InvalidRecord);
        }
        let generation = cursor.u64()?;
        let schema = cursor.u64()?;
        let sequence = cursor.u64()?;
        let collection = cursor.name()?;
        let index = cursor.name()?;
        let candidate_digest = cursor.array32()?;
        let watermark = if has_watermark == 1 { Some(cursor.u64()?) } else { None };
        if cursor.remaining() != 0 {
            return Err(JournalError::InvalidRecord);
        }
        let identity = LifecycleIdentity::new(collection, index, generation, schema, sequence)
            .map_err(|_| JournalError::InvalidRecord)?;
        if matches!(
            operation,
            LifecycleOperation::Build | LifecycleOperation::Watermark | LifecycleOperation::Verify
        ) && watermark.is_none()
        {
            return Err(JournalError::InvalidRecord);
        }
        Ok(DecodedRecord {
            identity,
            operation,
            from,
            to,
            watermark,
            candidate_digest,
            checksum: *blake3::hash(payload).as_bytes(),
        })
    }
}

fn put_name(out: &mut Vec<u8>, name: &str) -> Result<(), JournalError> {
    if name.is_empty() || name.len() > MAX_NAME_BYTES {
        return Err(JournalError::InvalidRecord);
    }
    let len = u16::try_from(name.len()).map_err(|_| JournalError::InvalidRecord)?;
    out.extend_from_slice(&len.to_le_bytes());
    out.extend_from_slice(name.as_bytes());
    Ok(())
}

fn digest_candidate(candidate: &Candidate) -> [u8; 32] {
    let mut bytes = Vec::new();
    bytes.extend_from_slice(candidate.collection().as_bytes());
    bytes.push(0);
    for column in candidate.columns() {
        bytes.extend_from_slice(column.field().as_bytes());
        bytes.push(column.role() as u8);
        bytes.push(0);
    }
    bytes.push(candidate.kind() as u8);
    bytes.push(candidate.source() as u8);
    bytes.push(u8::from(candidate.needs_descriptor()));
    bytes.extend_from_slice(&candidate.benefit_per_byte().to_bits().to_le_bytes());
    *blake3::hash(&bytes).as_bytes()
}

fn phase_code(phase: AutoIndexPhase) -> u8 {
    match phase {
        AutoIndexPhase::Observing => 1,
        AutoIndexPhase::Compressed => 2,
        AutoIndexPhase::Selected => 3,
        AutoIndexPhase::Building => 4,
        AutoIndexPhase::Shadowing => 5,
        AutoIndexPhase::Active => 6,
        AutoIndexPhase::Invisible => 7,
        AutoIndexPhase::Abandoned => 8,
    }
}
fn phase_from_code(code: u8) -> Option<AutoIndexPhase> {
    Some(match code {
        1 => AutoIndexPhase::Observing,
        2 => AutoIndexPhase::Compressed,
        3 => AutoIndexPhase::Selected,
        4 => AutoIndexPhase::Building,
        5 => AutoIndexPhase::Shadowing,
        6 => AutoIndexPhase::Active,
        7 => AutoIndexPhase::Invisible,
        8 => AutoIndexPhase::Abandoned,
        _ => return None,
    })
}

/// Decoded metadata; candidate bytes are represented by a digest.
#[derive(Clone, Debug)]
pub(crate) struct DecodedRecord {
    pub(crate) identity: LifecycleIdentity,
    pub(crate) operation: LifecycleOperation,
    pub(crate) from: AutoIndexPhase,
    pub(crate) to: AutoIndexPhase,
    pub(crate) watermark: Option<u64>,
    pub(crate) candidate_digest: [u8; 32],
    pub(crate) checksum: [u8; 32],
}

impl DecodedRecord {
    pub(crate) fn event(&self) -> LifecycleEvent {
        LifecycleEvent {
            identity: self.identity.clone(),
            operation: self.operation,
            from: self.from,
            to: self.to,
            watermark: self.watermark,
            checksum: self.checksum,
        }
    }
}

/// Journal refusal; storage details do not cross the optimizer seam.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub(crate) enum JournalError {
    #[default]
    Unavailable,
    InvalidRecord,
    RecordTooLarge,
    JournalTooLarge,
    TooManyRecords,
    SequenceConflict,
    Truncated,
    Corrupt,
    InvalidTransition,
}

/// In-memory byte journal used by the SQLite integration.
#[derive(Clone, Debug, Default)]
pub(crate) struct DurableLifecycleJournal {
    bytes: Vec<u8>,
    records: usize,
}

impl DurableLifecycleJournal {
    pub(crate) fn new() -> Self {
        Self::default()
    }
    pub(crate) fn bytes(&self) -> &[u8] {
        &self.bytes
    }
    pub(crate) fn append_record(&mut self, record: JournalRecord) -> Result<(), JournalError> {
        let frame = record.encode()?;
        if self.records >= MAX_JOURNAL_RECORDS {
            return Err(JournalError::TooManyRecords);
        }
        if self.bytes.len().saturating_add(frame.len()) > MAX_JOURNAL_BYTES {
            return Err(JournalError::JournalTooLarge);
        }
        self.bytes.extend_from_slice(&frame);
        self.records += 1;
        Ok(())
    }
    pub(crate) fn replay(&self) -> Result<ReplayState, JournalError> {
        ReplayState::from_bytes(&self.bytes)
    }
}

/// Port implemented by the SQLite publication journal.
pub(crate) trait LifecycleJournal {
    fn append(&mut self, record: JournalRecord) -> Result<(), JournalError>;
}
impl LifecycleJournal for DurableLifecycleJournal {
    fn append(&mut self, record: JournalRecord) -> Result<(), JournalError> {
        self.append_record(record)
    }
}

/// Recovered state and bounded audit events.
#[derive(Clone, Debug)]
pub(crate) struct ReplayState {
    states: BTreeMap<(String, String, u64, u64), LifecycleState>,
    events: VecDeque<LifecycleEvent>,
}

impl ReplayState {
    pub(crate) fn from_bytes(bytes: &[u8]) -> Result<Self, JournalError> {
        if bytes.len() > MAX_JOURNAL_BYTES {
            return Err(JournalError::JournalTooLarge);
        }
        let mut offset = 0;
        let mut count = 0;
        let mut states = BTreeMap::new();
        let mut events = VecDeque::new();
        while offset < bytes.len() {
            if count >= MAX_JOURNAL_RECORDS {
                return Err(JournalError::TooManyRecords);
            }
            if bytes.len() - offset < HEADER_BYTES {
                return Err(JournalError::Truncated);
            }
            let len = u32::from_le_bytes(
                bytes[offset + 5..offset + 9].try_into().map_err(|_| JournalError::Corrupt)?,
            ) as usize;
            let frame_len = HEADER_BYTES.checked_add(len).ok_or(JournalError::RecordTooLarge)?;
            if len > MAX_RECORD_BYTES || frame_len > bytes.len() - offset {
                return Err(JournalError::Truncated);
            }
            let record = JournalRecord::decode(&bytes[offset..offset + frame_len])?;
            let key = (
                record.identity.collection().to_owned(),
                record.identity.index().to_owned(),
                record.identity.generation(),
                record.identity.schema(),
            );
            let state =
                states.entry(key).or_insert_with(|| LifecycleState::new(record.identity.clone()));
            let applied = state.apply(&record).map_err(JournalError::from)?;
            if applied {
                events.push_back(record.event());
                while events.len() > super::lifecycle::MAX_AUDIT_EVENTS {
                    events.pop_front();
                }
            }
            offset += frame_len;
            count += 1;
        }
        Ok(Self { states, events })
    }
    pub(crate) fn state(&self, identity: &LifecycleIdentity) -> Option<&LifecycleState> {
        self.states.get(&(
            identity.collection().to_owned(),
            identity.index().to_owned(),
            identity.generation(),
            identity.schema(),
        ))
    }
    pub(crate) fn audit(&self) -> AuditView {
        AuditView { events: self.events.clone() }
    }
}

struct Cursor<'a> {
    bytes: &'a [u8],
    offset: usize,
}
impl<'a> Cursor<'a> {
    fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, offset: 0 }
    }
    fn byte(&mut self) -> Result<u8, JournalError> {
        let byte = *self.bytes.get(self.offset).ok_or(JournalError::Truncated)?;
        self.offset += 1;
        Ok(byte)
    }
    fn u64(&mut self) -> Result<u64, JournalError> {
        let end = self.offset.checked_add(8).ok_or(JournalError::Truncated)?;
        let value = u64::from_le_bytes(
            self.bytes
                .get(self.offset..end)
                .ok_or(JournalError::Truncated)?
                .try_into()
                .map_err(|_| JournalError::Truncated)?,
        );
        self.offset = end;
        Ok(value)
    }
    fn name(&mut self) -> Result<String, JournalError> {
        let len = usize::from(u16::from_le_bytes([self.byte()?, self.byte()?]));
        let end = self.offset.checked_add(len).ok_or(JournalError::Truncated)?;
        let value =
            std::str::from_utf8(self.bytes.get(self.offset..end).ok_or(JournalError::Truncated)?)
                .map_err(|_| JournalError::InvalidRecord)?
                .to_owned();
        self.offset = end;
        if value.is_empty() || value.len() > MAX_NAME_BYTES {
            return Err(JournalError::InvalidRecord);
        }
        Ok(value)
    }
    fn array32(&mut self) -> Result<[u8; 32], JournalError> {
        let end = self.offset.checked_add(32).ok_or(JournalError::Truncated)?;
        let value = self
            .bytes
            .get(self.offset..end)
            .ok_or(JournalError::Truncated)?
            .try_into()
            .map_err(|_| JournalError::Truncated)?;
        self.offset = end;
        Ok(value)
    }
    fn remaining(&self) -> usize {
        self.bytes.len() - self.offset
    }
}

impl From<LifecycleError> for JournalError {
    fn from(error: LifecycleError) -> Self {
        match error {
            LifecycleError::InvalidTransition => Self::InvalidTransition,
            LifecycleError::SequenceConflict => Self::SequenceConflict,
            _ => Self::InvalidRecord,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::{
        AutoIndexPhase, DurableLifecycleJournal, JournalError, JournalRecord, LifecycleIdentity,
        LifecycleOperation,
    };
    fn record(
        sequence: u64,
        operation: LifecycleOperation,
        from: AutoIndexPhase,
        to: AutoIndexPhase,
        watermark: Option<super::super::build::BuildWatermark>,
    ) -> JournalRecord {
        JournalRecord::new(
            LifecycleIdentity::new("users", "idx_users", 1, 7, sequence).unwrap(),
            operation,
            from,
            to,
            watermark,
            None,
        )
        .unwrap()
    }
    #[test]
    fn duplicate_replay_is_idempotent() {
        let mut journal = DurableLifecycleJournal::new();
        journal
            .append_record(record(
                1,
                LifecycleOperation::Observe,
                AutoIndexPhase::Observing,
                AutoIndexPhase::Observing,
                None,
            ))
            .unwrap();
        let bytes = journal.bytes().to_vec();
        let mut duplicated = bytes.clone();
        duplicated.extend_from_slice(&bytes);
        let replay = super::ReplayState::from_bytes(&duplicated).unwrap();
        assert_eq!(replay.audit().len(), 1);
    }
    #[test]
    fn corruption_and_truncation_refuse_replay() {
        let mut journal = DurableLifecycleJournal::new();
        journal
            .append_record(record(
                1,
                LifecycleOperation::Observe,
                AutoIndexPhase::Observing,
                AutoIndexPhase::Observing,
                None,
            ))
            .unwrap();
        let mut corrupt = journal.bytes().to_vec();
        let last = corrupt.len() - 1;
        corrupt[last] ^= 1;
        assert!(matches!(super::ReplayState::from_bytes(&corrupt), Err(JournalError::Corrupt)));
        let truncated = &journal.bytes()[..journal.bytes().len() - 1];
        assert!(matches!(super::ReplayState::from_bytes(truncated), Err(JournalError::Truncated)));
    }

    #[test]
    fn unknown_phase_code_refuses_replay_even_with_valid_checksum() {
        let mut frame = record(
            1,
            LifecycleOperation::Observe,
            AutoIndexPhase::Observing,
            AutoIndexPhase::Observing,
            None,
        )
        .encode()
        .unwrap();
        frame[super::HEADER_BYTES + 1] = 0;
        let checksum = *blake3::hash(&frame[super::HEADER_BYTES..]).as_bytes();
        frame[9..41].copy_from_slice(&checksum);

        assert!(matches!(super::ReplayState::from_bytes(&frame), Err(JournalError::InvalidRecord)));
    }
}
