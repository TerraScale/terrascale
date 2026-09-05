//! PN-counter state: independently monotonic positive and negative actor totals.

use crate::codec::{DecodeError, DecodeLimits, Decoder, encode_u64};
use std::collections::BTreeMap;
use terrabase_causal::{ActorId, CanonicalEncode, ReplicaEpoch, ReplicaId};

/// Maximum retained actor incarnations in one counter. Retiring a component
/// requires coordinated compaction; silently dropping one changes the value.
pub const MAX_COUNTER_ACTORS: usize = 1024;

/// A bounded PN-counter. Each actor has exactly one serialized mutation owner.
/// Replication joins cumulative components; it never replays `add` as a delta.
/// Accepted state fits both unsigned component sums and the signed result.
/// Independent valid states can exceed those bounds when joined; callers must
/// quarantine a refused remote state instead of acknowledging or truncating it.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct PnCounter {
    components: BTreeMap<ActorId, (u128, u128)>,
}

/// A counter operation that cannot be represented within its resource bounds.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CounterError {
    /// A component, component sum, or signed value would overflow.
    Overflow,
    /// Too many actor incarnations. No state was changed.
    ActorLimit,
}

impl PnCounter {
    /// Applies one local signed delta. The caller durably deduplicates local
    /// requests and serializes updates for the actor. Zero changes nothing.
    ///
    /// # Errors
    /// Rejects component or materialization overflow and actor-count exhaustion
    /// atomically. Rejected updates do not allocate an actor or consume a delta.
    pub fn add(&mut self, actor: ActorId, delta: i128) -> Result<(), CounterError> {
        if delta == 0 {
            return Ok(());
        }
        let mut component = self.components.get(&actor).copied().unwrap_or_default();
        if !self.components.contains_key(&actor) && self.components.len() == MAX_COUNTER_ACTORS {
            return Err(CounterError::ActorLimit);
        }
        let total = if delta > 0 { &mut component.0 } else { &mut component.1 };
        *total = total.checked_add(delta.unsigned_abs()).ok_or(CounterError::Overflow)?;
        checked_value(
            self.components
                .iter()
                .filter_map(|(key, value)| (*key != actor).then_some(value))
                .chain(std::iter::once(&component)),
        )?;
        self.components.insert(actor, component);
        Ok(())
    }

    /// Joins cumulative actor totals by componentwise maximum. Neither input
    /// changes. Delivery order and duplicate state delivery cannot change the join.
    ///
    /// # Errors
    /// Rejects a union exceeding the actor bound or numeric range, without
    /// changing either input or discarding components.
    pub fn join(&self, other: &Self) -> Result<Self, CounterError> {
        let additional =
            other.components.keys().filter(|actor| !self.components.contains_key(actor)).count();
        if self.components.len() + additional > MAX_COUNTER_ACTORS {
            return Err(CounterError::ActorLimit);
        }
        let mut joined = self.clone();
        for (actor, (positive, negative)) in &other.components {
            let component = joined.components.entry(*actor).or_default();
            component.0 = component.0.max(*positive);
            component.1 = component.1.max(*negative);
        }
        joined.value()?;
        Ok(joined)
    }

    /// Returns the checked signed difference of the two checked component sums.
    ///
    /// # Errors
    /// Rejects unsigned sum overflow or a difference outside the signed range.
    pub fn value(&self) -> Result<i128, CounterError> {
        checked_value(self.components.values())
    }

    /// Returns retained actor incarnation count.
    #[must_use]
    pub fn actor_count(&self) -> usize {
        self.components.len()
    }

    /// Decodes canonical counter bytes under caller and intrinsic bounds.
    ///
    /// # Errors
    /// Rejects malformed, oversized, noncanonical, overflowing, or trailing
    /// input before accepting a state. Zero components and duplicate actors
    /// are noncanonical.
    pub fn decode(bytes: &[u8], limits: DecodeLimits) -> Result<Self, DecodeError> {
        let mut reader = Decoder::new(bytes, limits)?;
        if reader.byte()? != 1 {
            return Err(DecodeError::UnsupportedVersion);
        }
        if reader.byte()? != 3 {
            return Err(DecodeError::WrongDatatype);
        }
        let count = usize::try_from(reader.leb_u64()?).map_err(|_| DecodeError::LimitExceeded)?;
        if count > MAX_COUNTER_ACTORS.min(limits.max_context_actors) {
            return Err(DecodeError::LimitExceeded);
        }
        let mut components = BTreeMap::new();
        let mut previous = None;
        for _ in 0..count {
            let actor = ActorId::new(
                ReplicaId::from_bytes(reader.fixed()?),
                ReplicaEpoch::new(reader.leb_u64()?).map_err(|_| DecodeError::InvalidValue)?,
            );
            let positive = decode_component(&mut reader)?;
            let negative = decode_component(&mut reader)?;
            if previous.is_some_and(|previous| previous >= actor)
                || (positive == 0 && negative == 0)
            {
                return Err(DecodeError::NonCanonicalContext);
            }
            previous = Some(actor);
            components.insert(actor, (positive, negative));
        }
        if !reader.done() {
            return Err(DecodeError::TrailingBytes);
        }
        let state = Self { components };
        state.value().map_err(|_| DecodeError::InvalidValue)?;
        Ok(state)
    }
}

impl CanonicalEncode for PnCounter {
    fn encode_canonical(&self, output: &mut Vec<u8>) {
        output.extend_from_slice(&[1, 3]);
        encode_u64(self.components.len() as u64, output);
        for (actor, (positive, negative)) in &self.components {
            output.extend_from_slice(actor.replica().as_bytes());
            encode_u64(actor.epoch().get(), output);
            encode_component(*positive, output);
            encode_component(*negative, output);
        }
    }
}

fn checked_value<'a>(
    mut components: impl Iterator<Item = &'a (u128, u128)>,
) -> Result<i128, CounterError> {
    let (positive, negative) =
        components.try_fold((0_u128, 0_u128), |(positive, negative), (p, n)| {
            Ok((
                positive.checked_add(*p).ok_or(CounterError::Overflow)?,
                negative.checked_add(*n).ok_or(CounterError::Overflow)?,
            ))
        })?;
    if positive >= negative {
        i128::try_from(positive - negative).map_err(|_| CounterError::Overflow)
    } else {
        let magnitude = negative - positive;
        if magnitude == 1_u128 << 127 {
            Ok(i128::MIN)
        } else {
            i128::try_from(magnitude).map(|value| -value).map_err(|_| CounterError::Overflow)
        }
    }
}

fn encode_component(mut value: u128, output: &mut Vec<u8>) {
    loop {
        let byte = (value & 0x7f) as u8;
        value >>= 7;
        output.push(if value == 0 { byte } else { byte | 0x80 });
        if value == 0 {
            return;
        }
    }
}

fn decode_component(reader: &mut Decoder<'_>) -> Result<u128, DecodeError> {
    let mut value = 0_u128;
    for index in 0..19 {
        let byte = reader.byte()?;
        let payload = u128::from(byte & 0x7f);
        if index == 18 && (byte & 0xfc) != 0 {
            return Err(DecodeError::InvalidLeb128);
        }
        value |= payload << (index * 7);
        if byte & 0x80 == 0 {
            if index > 0 && payload == 0 {
                return Err(DecodeError::InvalidLeb128);
            }
            return Ok(value);
        }
    }
    Err(DecodeError::InvalidLeb128)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn actor(id: u128, epoch: u64) -> ActorId {
        ActorId::new(ReplicaId::from_bytes(id.to_be_bytes()), ReplicaEpoch::new(epoch).unwrap())
    }

    #[test]
    fn joins_obey_algebra_and_preserve_concurrent_signed_updates() {
        let mut states = vec![PnCounter::default()];
        for id in 1..=3 {
            for delta in [-7, 5] {
                let mut state = PnCounter::default();
                state.add(actor(id, 1), delta).unwrap();
                states.push(state);
            }
        }
        for left in &states {
            assert_eq!(left.join(left).unwrap(), *left);
            for right in &states {
                assert_eq!(left.join(right).unwrap(), right.join(left).unwrap());
                for third in &states {
                    assert_eq!(
                        left.join(right).unwrap().join(third).unwrap(),
                        left.join(&right.join(third).unwrap()).unwrap()
                    );
                }
            }
        }
        let mut base = PnCounter::default();
        base.add(actor(1, 1), 10).unwrap();
        let mut left = base.clone();
        left.add(actor(2, 1), 5).unwrap();
        let mut right = base;
        right.add(actor(3, 1), -7).unwrap();
        let joined = left.join(&right).unwrap();
        assert_eq!(joined.value(), Ok(8));
        assert_eq!(joined.join(&left).unwrap().join(&right).unwrap(), joined);
        let mut restarted = joined.clone();
        restarted.add(actor(2, 2), -3).unwrap();
        assert_eq!(restarted.join(&joined).unwrap().value(), Ok(5));
        assert_eq!(restarted.actor_count(), 4);
    }

    #[test]
    fn mutation_and_merge_limits_are_atomic() {
        let mut state = PnCounter::default();
        state.add(actor(1, 1), i128::MAX).unwrap();
        let before = state.clone();
        assert_eq!(state.value(), Ok(i128::MAX));
        assert_eq!(
            PnCounter::decode(&state.canonical_bytes(), DecodeLimits::default()).unwrap(),
            state
        );
        assert_eq!(state.add(actor(1, 1), 2), Err(CounterError::Overflow));
        assert_eq!(state, before);
        let mut negative = PnCounter::default();
        negative.add(actor(1, 1), i128::MIN).unwrap();
        assert_eq!(negative.value(), Ok(i128::MIN));
        let before = negative.clone();
        assert_eq!(negative.add(actor(1, 1), i128::MIN), Err(CounterError::Overflow));
        assert_eq!(negative, before);
        let mut state = PnCounter::default();
        state.add(actor(1, 1), 1).unwrap();
        for id in 2..=MAX_COUNTER_ACTORS {
            state.add(actor(id as u128, 1), 1).unwrap();
        }
        let before = state.clone();
        assert_eq!(state.add(actor(2000, 1), 1), Err(CounterError::ActorLimit));
        state.add(actor(2000, 1), 0).unwrap();
        assert_eq!(state, before);
        let mut extra = PnCounter::default();
        extra.add(actor(2000, 1), 1).unwrap();
        assert_eq!(state.join(&extra), Err(CounterError::ActorLimit));
        assert_eq!(state, before);
    }

    #[test]
    fn overflow_is_rejected_at_every_state_admission_boundary() {
        let limits = DecodeLimits::default();
        let mut maximum = PnCounter::default();
        maximum.add(actor(1, 1), i128::MAX).unwrap();
        let before = maximum.clone();
        assert_eq!(maximum.add(actor(2, 1), 1), Err(CounterError::Overflow));
        assert_eq!(maximum, before);
        assert_eq!(maximum.actor_count(), 1);
        let mut one = PnCounter::default();
        one.add(actor(2, 1), 1).unwrap();
        assert_eq!(maximum.join(&one), Err(CounterError::Overflow));
        assert_eq!(one.join(&maximum), Err(CounterError::Overflow));
        assert_eq!(maximum, before);
        assert_eq!(one.value(), Ok(1));

        let mut saturated =
            PnCounter { components: BTreeMap::from([(actor(1, 1), (u128::MAX, u128::MAX))]) };
        assert_eq!(saturated.value(), Ok(0));
        assert_eq!(PnCounter::decode(&saturated.canonical_bytes(), limits).unwrap(), saturated);
        let before = saturated.clone();
        for key in [actor(1, 1), actor(2, 1)] {
            for delta in [-1, 1] {
                assert_eq!(saturated.add(key, delta), Err(CounterError::Overflow));
                assert_eq!(saturated, before);
            }
        }
        assert_eq!(saturated.join(&one), Err(CounterError::Overflow));
        for components in [
            BTreeMap::from([(actor(1, 1), (u128::MAX, 0))]),
            BTreeMap::from([(actor(1, 1), (0, u128::MAX))]),
            BTreeMap::from([(actor(1, 1), (u128::MAX, u128::MAX)), (actor(2, 1), (1, 1))]),
        ] {
            let invalid = PnCounter { components };
            assert_eq!(invalid.value(), Err(CounterError::Overflow));
            assert_eq!(
                PnCounter::decode(&invalid.canonical_bytes(), limits),
                Err(DecodeError::InvalidValue)
            );
        }
    }

    #[test]
    fn component_codec_checks_the_full_unsigned_range() {
        for value in [0, 127, 128, u128::from(u64::MAX) + 1, u128::MAX] {
            let mut bytes = Vec::new();
            encode_component(value, &mut bytes);
            let mut reader = Decoder::new(&bytes, DecodeLimits::default()).unwrap();
            assert_eq!(decode_component(&mut reader), Ok(value));
            assert!(reader.done());
        }
        for bytes in [vec![0x80, 0], vec![0xff; 19], {
            let mut bytes = vec![0xff; 18];
            bytes.push(4);
            bytes
        }] {
            let mut reader = Decoder::new(&bytes, DecodeLimits::default()).unwrap();
            assert_eq!(decode_component(&mut reader), Err(DecodeError::InvalidLeb128));
        }
    }

    #[test]
    fn canonical_codec_has_stable_bytes_and_rejects_malformed_state() {
        let mut state = PnCounter::default();
        state.add(actor(1, 1), 5).unwrap();
        state.add(actor(1, 1), -2).unwrap();
        let mut golden = vec![1, 3, 1];
        golden.extend_from_slice(&1_u128.to_be_bytes());
        golden.extend_from_slice(&[1, 5, 2]);
        assert_eq!(state.canonical_bytes(), golden);
        let limits = DecodeLimits::default();
        assert_eq!(PnCounter::decode(&golden, limits).unwrap(), state);
        for length in 0..golden.len() {
            assert!(PnCounter::decode(&golden[..length], limits).is_err());
        }
        let mut trailing = golden.clone();
        trailing.push(0);
        assert_eq!(PnCounter::decode(&trailing, limits), Err(DecodeError::TrailingBytes));
        let mut duplicate = golden.clone();
        duplicate[2] = 2;
        duplicate.extend_from_slice(&golden[3..]);
        assert_eq!(PnCounter::decode(&duplicate, limits), Err(DecodeError::NonCanonicalContext));
        let mut zero = golden.clone();
        zero[20..].fill(0);
        assert_eq!(PnCounter::decode(&zero, limits), Err(DecodeError::NonCanonicalContext));
        let mut invalid_epoch = golden.clone();
        invalid_epoch[19] = 0;
        assert_eq!(PnCounter::decode(&invalid_epoch, limits), Err(DecodeError::InvalidValue));
        let bounded = DecodeLimits { max_context_actors: 0, ..limits };
        assert_eq!(PnCounter::decode(&golden, bounded), Err(DecodeError::LimitExceeded));
    }

    #[test]
    fn restored_components_preserve_updates_and_reject_noncanonical_encodings() {
        let limits = DecodeLimits::default();
        let mut state = PnCounter::default();
        for id in (1..=MAX_COUNTER_ACTORS).rev() {
            state.add(actor(id as u128, u64::MAX), -1).unwrap();
        }
        let bytes = state.canonical_bytes();
        let restored = PnCounter::decode(&bytes, limits).unwrap();
        assert_eq!(restored, state);
        assert_eq!(restored.canonical_bytes(), bytes);
        assert_eq!(restored.value(), Ok(-(MAX_COUNTER_ACTORS as i128)));
        let mut updated = restored;
        updated.add(actor(1, u64::MAX), 5).unwrap();
        assert_eq!(updated.join(&state).unwrap(), updated);
        assert_eq!(state.join(&updated).unwrap(), updated);
        assert_eq!(
            PnCounter::decode(&bytes, DecodeLimits { max_bytes: bytes.len() - 1, ..limits }),
            Err(DecodeError::LimitExceeded)
        );
        // The intrinsic bound still applies when the caller allows more actors.
        let mut oversized = vec![1, 3];
        encode_u64(MAX_COUNTER_ACTORS as u64 + 1, &mut oversized);
        assert_eq!(PnCounter::decode(&oversized, limits), Err(DecodeError::LimitExceeded));
        // A non-minimal count cannot create a second encoding of the empty state.
        assert_eq!(PnCounter::decode(&[1, 3, 0x80, 0], limits), Err(DecodeError::InvalidLeb128));
        assert_eq!(PnCounter::decode(&[2, 3, 0], limits), Err(DecodeError::UnsupportedVersion));
        assert_eq!(PnCounter::decode(&[1, 2, 0], limits), Err(DecodeError::WrongDatatype));
        assert_eq!(PnCounter::decode(&[1, 3, 0], limits).unwrap(), PnCounter::default());

        let mut pair = PnCounter::default();
        pair.add(actor(1, 1), 1).unwrap();
        pair.add(actor(2, 1), 1).unwrap();
        let mut reversed = pair.canonical_bytes();
        let (first, second) = reversed[3..].split_at_mut(19);
        first.swap_with_slice(second);
        assert_eq!(PnCounter::decode(&reversed, limits), Err(DecodeError::NonCanonicalContext));
    }
}
