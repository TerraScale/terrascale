//! LWW and multi-value register state and join algebra.

use terrabase_causal::{
    ActorId, CausalContext, CanonicalEncode, DottedVersion, Dot, HlcTimestamp, MutationOrdinal,
    canonical_hash,
};

use crate::codec::{CanonicalValue, DecodeError, DecodeLimits, Decoder, encode_u64};

const CODEC_VERSION: u8 = 1;
const LWW_KIND: u8 = 1;
const MV_KIND: u8 = 2;
const LIVE_VALUE: u8 = 1;
const REMOVE_VALUE: u8 = 0;

/// The causal identity of one register candidate.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct CausalIdentity {
    dot: Dot,
    mutation_ordinal: MutationOrdinal,
}

impl CausalIdentity {
    /// Creates an identity from a dot and its commit-local ordinal.
    #[must_use]
    pub const fn new(dot: Dot, mutation_ordinal: MutationOrdinal) -> Self {
        Self { dot, mutation_ordinal }
    }

    /// Returns the causal dot.
    #[must_use]
    pub const fn dot(self) -> Dot {
        self.dot
    }

    /// Returns the commit-local mutation ordinal.
    #[must_use]
    pub const fn mutation_ordinal(self) -> MutationOrdinal {
        self.mutation_ordinal
    }
}

/// One register assignment or removal, including its causal evidence.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Candidate<T: CanonicalValue> {
    value: Option<T>,
    version: DottedVersion,
    mutation_ordinal: MutationOrdinal,
    hlc: HlcTimestamp,
}

impl<T: CanonicalValue> Candidate<T> {
    /// Creates one candidate. `None` is a field removal candidate.
    #[must_use]
    pub const fn new(
        value: Option<T>,
        version: DottedVersion,
        mutation_ordinal: MutationOrdinal,
        hlc: HlcTimestamp,
    ) -> Self {
        Self { value, version, mutation_ordinal, hlc }
    }

    /// Returns the candidate's optional value. `None` means removal.
    #[must_use]
    pub const fn value(&self) -> Option<&T> {
        self.value.as_ref()
    }

    /// Reports whether this candidate is a removal.
    #[must_use]
    pub const fn is_remove(&self) -> bool {
        self.value.is_none()
    }

    /// Returns the dotted causal version.
    #[must_use]
    pub const fn version(&self) -> &DottedVersion {
        &self.version
    }

    /// Returns the event dot.
    #[must_use]
    pub const fn dot(&self) -> Dot {
        self.version.dot()
    }

    /// Returns the mutation ordinal.
    #[must_use]
    pub const fn mutation_ordinal(&self) -> MutationOrdinal {
        self.mutation_ordinal
    }

    /// Returns the candidate identity.
    #[must_use]
    pub const fn identity(&self) -> CausalIdentity {
        CausalIdentity::new(self.dot(), self.mutation_ordinal)
    }

    /// Returns the HLC metadata used only for concurrent ordering.
    #[must_use]
    pub const fn hlc(&self) -> HlcTimestamp {
        self.hlc
    }

    fn key(&self) -> (HlcTimestamp, terrabase_causal::ReplicaId, terrabase_causal::ReplicaEpoch, terrabase_causal::OriginCounter, MutationOrdinal) {
        let actor = self.dot().actor();
        (self.hlc, actor.replica(), actor.epoch(), self.dot().counter(), self.mutation_ordinal)
    }
}

/// A last-writer-wins register with causal evidence for discarded candidates.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LwwRegister<T: CanonicalValue> {
    context: CausalContext,
    winner: Option<Candidate<T>>,
}

impl<T: CanonicalValue> Default for LwwRegister<T> {
    fn default() -> Self {
        Self::empty()
    }
}

impl<T: CanonicalValue> LwwRegister<T> {
    /// Creates an empty register.
    #[must_use]
    pub const fn empty() -> Self {
        Self { context: CausalContext::new(), winner: None }
    }

    /// Creates a register containing one candidate.
    #[must_use]
    pub fn from_candidate(candidate: Candidate<T>) -> Self {
        let mut context = candidate.version.context().clone();
        context.insert(candidate.dot());
        Self { context, winner: Some(candidate) }
    }

    /// Returns the joined causal context, including discarded candidate dots.
    #[must_use]
    pub const fn context(&self) -> &CausalContext {
        &self.context
    }

    /// Returns the deterministic winning candidate, including a removal.
    #[must_use]
    pub const fn winner(&self) -> Option<&Candidate<T>> {
        self.winner.as_ref()
    }

    /// Returns the visible winning value, or `None` for empty/removal state.
    #[must_use]
    pub fn materialize(&self) -> Option<&T> {
        self.winner.as_ref().and_then(Candidate::value)
    }

    /// Joins two states using causal dominance before the HLC tie-break.
    #[must_use]
    pub fn join(&self, other: &Self) -> Self {
        let mut context = self.context.clone();
        context.union(&other.context);
        let mut candidates = Vec::with_capacity(2);
        if let Some(candidate) = self.winner.as_ref() {
            if other.winner.as_ref().is_some_and(|right| right.identity() == candidate.identity())
                || !other.context.contains(candidate.dot())
            {
                candidates.push(candidate.clone());
            }
        }
        if let Some(candidate) = other.winner.as_ref() {
            if self.winner.as_ref().is_some_and(|left| left.identity() == candidate.identity())
                || !self.context.contains(candidate.dot())
            {
                if !candidates.iter().any(|existing: &Candidate<T>| existing.identity() == candidate.identity()) {
                    candidates.push(candidate.clone());
                }
            }
        }
        let winner = candidates.into_iter().max_by(|left, right| left.key().cmp(&right.key()));
        Self { context, winner }
    }

    /// Returns BLAKE3 over the domain-separated canonical register bytes.
    pub fn canonical_hash(&self) -> Result<[u8; 32], terrabase_causal::HashError> {
        canonical_hash("crdt-register", self)
    }

    pub(crate) fn decode(bytes: &[u8], limits: DecodeLimits) -> Result<Self, DecodeError> {
        let mut reader = Decoder::new(bytes, limits)?;
        let state = decode_state(&mut reader, limits, LWW_KIND)?;
        if !reader.done() {
            return Err(DecodeError::TrailingBytes);
        }
        let (context, candidates) = state;
        let winner = candidates.into_iter().next();
        Ok(Self { context, winner })
    }
}

/// A multi-value register retaining all causally maximal candidates.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MvRegister<T: CanonicalValue> {
    context: CausalContext,
    candidates: Vec<Candidate<T>>,
}

impl<T: CanonicalValue> Default for MvRegister<T> {
    fn default() -> Self {
        Self::empty()
    }
}

impl<T: CanonicalValue> MvRegister<T> {
    /// Creates an empty register.
    #[must_use]
    pub const fn empty() -> Self {
        Self { context: CausalContext::new(), candidates: Vec::new() }
    }

    /// Creates a register containing one candidate.
    #[must_use]
    pub fn from_candidate(candidate: Candidate<T>) -> Self {
        let mut context = candidate.version.context().clone();
        context.insert(candidate.dot());
        Self { context, candidates: vec![candidate] }
    }

    /// Returns the joined causal context, including discarded candidate dots.
    #[must_use]
    pub const fn context(&self) -> &CausalContext {
        &self.context
    }

    /// Returns causally maximal candidates in canonical HLC-key order.
    #[must_use]
    pub fn candidates(&self) -> &[Candidate<T>] {
        &self.candidates
    }

    /// Returns visible values and their causal identities in canonical order.
    #[must_use]
    pub fn materialize(&self) -> Vec<(CausalIdentity, T)> {
        self.candidates
            .iter()
            .filter_map(|candidate| candidate.value().cloned().map(|value| (candidate.identity(), value)))
            .collect()
    }

    /// Joins two states, removing only causally dominated candidates.
    #[must_use]
    pub fn join(&self, other: &Self) -> Self {
        let mut context = self.context.clone();
        context.union(&other.context);
        let mut candidates = Vec::with_capacity(self.candidates.len() + other.candidates.len());
        for candidate in self.candidates.iter().chain(other.candidates.iter()) {
            let duplicate = candidates.iter().any(|existing: &Candidate<T>| existing.identity() == candidate.identity());
            if duplicate {
                continue;
            }
            let observed_by_other = if self.candidates.iter().any(|existing| existing.identity() == candidate.identity()) {
                other.candidates.iter().any(|existing| existing.identity() == candidate.identity())
                    || other.context.contains(candidate.dot())
            } else {
                self.context.contains(candidate.dot())
            };
            if !observed_by_other {
                candidates.push(candidate.clone());
            }
        }
        candidates.sort_by(|left, right| left.key().cmp(&right.key()));
        Self { context, candidates }
    }

    /// Returns BLAKE3 over the domain-separated canonical register bytes.
    pub fn canonical_hash(&self) -> Result<[u8; 32], terrabase_causal::HashError> {
        canonical_hash("crdt-register", self)
    }

    pub(crate) fn decode(bytes: &[u8], limits: DecodeLimits) -> Result<Self, DecodeError> {
        let mut reader = Decoder::new(bytes, limits)?;
        let (context, candidates) = decode_state(&mut reader, limits, MV_KIND)?;
        if !reader.done() {
            return Err(DecodeError::TrailingBytes);
        }
        Ok(Self { context, candidates })
    }
}

impl<T: CanonicalValue> CanonicalEncode for LwwRegister<T> {
    fn encode_canonical(&self, output: &mut Vec<u8>) {
        encode_state(output, LWW_KIND, &self.context, self.winner.iter())
    }
}

impl<T: CanonicalValue> CanonicalEncode for MvRegister<T> {
    fn encode_canonical(&self, output: &mut Vec<u8>) {
        encode_state(output, MV_KIND, &self.context, self.candidates.iter())
    }
}

fn encode_state<'a, T: CanonicalValue + 'a>(
    output: &mut Vec<u8>,
    kind: u8,
    context: &CausalContext,
    candidates: impl Iterator<Item = &'a Candidate<T>>,
) {
    output.push(CODEC_VERSION);
    output.push(kind);
    output.push(T::TYPE_TAG);
    encode_context(context, output);
    let candidates: Vec<_> = candidates.collect();
    encode_u64(candidates.len() as u64, output);
    for candidate in candidates {
        encode_candidate(candidate, output);
    }
}

fn encode_candidate<T: CanonicalValue>(candidate: &Candidate<T>, output: &mut Vec<u8>) {
    output.push(if candidate.is_remove() { REMOVE_VALUE } else { LIVE_VALUE });
    output.extend_from_slice(candidate.dot().actor().replica().as_bytes());
    encode_u64(candidate.dot().actor().epoch().get(), output);
    encode_u64(candidate.dot().counter().get(), output);
    encode_u64(candidate.mutation_ordinal().get() as u64, output);
    encode_context(candidate.version.context(), output);
    encode_u64(candidate.hlc().physical_unix_millis(), output);
    encode_u64(u64::from(candidate.hlc().logical()), output);
    if let Some(value) = candidate.value() {
        output.push(T::TYPE_TAG);
        value.encode_value(output);
    }
}

fn encode_context(context: &CausalContext, output: &mut Vec<u8>) {
    let actors: Vec<_> = context.actors().collect();
    encode_u64(actors.len() as u64, output);
    for (actor, progress) in actors {
        output.extend_from_slice(actor.replica().as_bytes());
        encode_u64(actor.epoch().get(), output);
        encode_u64(progress.through(), output);
        encode_u64(progress.intervals().len() as u64, output);
        for interval in progress.intervals() {
            encode_u64(interval.first(), output);
            encode_u64(interval.last(), output);
        }
    }
}

fn decode_state<T: CanonicalValue>(
    reader: &mut Decoder<'_>,
    limits: DecodeLimits,
    expected_kind: u8,
) -> Result<(CausalContext, Vec<Candidate<T>>), DecodeError> {
    if reader.byte()? != CODEC_VERSION {
        return Err(DecodeError::UnsupportedVersion);
    }
    if reader.byte()? != expected_kind {
        return Err(DecodeError::WrongDatatype);
    }
    if reader.byte()? != T::TYPE_TAG {
        return Err(DecodeError::WrongDatatype);
    }
    let context = decode_context(reader, limits)?;
    let count = bounded_count(reader.leb_u64()?, limits.max_candidates)?;
    let mut candidates = Vec::with_capacity(count);
    for _ in 0..count {
        let candidate = decode_candidate(reader, limits)?;
        if !context.contains(candidate.dot()) || !context.dominates(candidate.version.context()) {
            return Err(DecodeError::NonCanonicalContext);
        }
        if candidates.iter().any(|existing: &Candidate<T>| existing.identity() == candidate.identity()) {
            return Err(DecodeError::InvalidValue);
        }
        candidates.push(candidate);
    }
    if expected_kind == LWW_KIND && candidates.len() > 1 {
        return Err(DecodeError::InvalidValue);
    }
    if expected_kind == MV_KIND {
        let mut sorted = candidates.clone();
        sorted.sort_by(|left, right| left.key().cmp(&right.key()));
        if sorted != candidates {
            return Err(DecodeError::InvalidValue);
        }
    }
    Ok((context, candidates))
}

fn decode_candidate<T: CanonicalValue>(
    reader: &mut Decoder<'_>,
    limits: DecodeLimits,
) -> Result<Candidate<T>, DecodeError> {
    let value_kind = reader.byte()?;
    if value_kind != LIVE_VALUE && value_kind != REMOVE_VALUE {
        return Err(DecodeError::InvalidValue);
    }
    let replica = terrabase_causal::ReplicaId::from_bytes(reader.fixed()?);
    let epoch = terrabase_causal::ReplicaEpoch::new(reader.leb_u64()?).map_err(|_| DecodeError::InvalidValue)?;
    let counter = terrabase_causal::OriginCounter::new(reader.leb_u64()?).map_err(|_| DecodeError::InvalidValue)?;
    let ordinal = MutationOrdinal::new(u32::try_from(reader.leb_u64()?).map_err(|_| DecodeError::InvalidValue)?);
    let context = decode_context(reader, limits)?;
    let physical = reader.leb_u64()?;
    let logical = u32::try_from(reader.leb_u64()?).map_err(|_| DecodeError::InvalidValue)?;
    let value = if value_kind == LIVE_VALUE {
        if reader.byte()? != T::TYPE_TAG {
            return Err(DecodeError::WrongDatatype);
        }
        Some(T::decode_value(reader, &limits)?)
    } else {
        None
    };
    let actor = ActorId::new(replica, epoch);
    let dot = Dot::new(actor, counter);
    Ok(Candidate::new(value, DottedVersion::new(context, dot), ordinal, HlcTimestamp::new(physical, logical)))
}

fn decode_context(reader: &mut Decoder<'_>, limits: DecodeLimits) -> Result<CausalContext, DecodeError> {
    let actor_count = bounded_count(reader.leb_u64()?, limits.max_context_actors)?;
    let mut context = CausalContext::new();
    let mut previous = None;
    for _ in 0..actor_count {
        let actor = ActorId::new(
            terrabase_causal::ReplicaId::from_bytes(reader.fixed()?),
            terrabase_causal::ReplicaEpoch::new(reader.leb_u64()?).map_err(|_| DecodeError::InvalidValue)?,
        );
        if previous.is_some_and(|previous| previous >= actor) {
            return Err(DecodeError::NonCanonicalContext);
        }
        previous = Some(actor);
        let through = reader.leb_u64()?;
        let interval_count = bounded_count(reader.leb_u64()?, limits.max_intervals_per_actor)?;
        let mut progress = terrabase_causal::ActorProgress::new();
        if through != 0 {
            progress.insert_interval(terrabase_causal::CounterInterval::new(1, through).map_err(|_| DecodeError::InvalidValue)?);
        }
        let mut last: Option<u64> = None;
        for _ in 0..interval_count {
            let interval = terrabase_causal::CounterInterval::new(reader.leb_u64()?, reader.leb_u64()?)
                .map_err(|_| DecodeError::InvalidValue)?;
            if interval.first() <= through || last.is_some_and(|last| interval.first() <= last.saturating_add(1)) {
                return Err(DecodeError::NonCanonicalContext);
            }
            last = Some(interval.last());
            progress.insert_interval(interval);
        }
        if progress.through() != through || progress.intervals().len() != interval_count {
            return Err(DecodeError::NonCanonicalContext);
        }
        context.insert_progress(actor, &progress);
    }
    Ok(context)
}

fn bounded_count(value: u64, maximum: usize) -> Result<usize, DecodeError> {
    let value = usize::try_from(value).map_err(|_| DecodeError::LimitExceeded)?;
    if value > maximum {
        return Err(DecodeError::LimitExceeded);
    }
    Ok(value)
}
