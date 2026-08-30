//! Cryptographically secure monotonic ULID generation (ADR 0027
//! 'Generation').
//!
//! ADR 0027 requires "a cryptographically secure monotonic ULID generator"
//! and the engine-core dependency budget (see the frozen table in
//! `tests/architecture/dependency_direction.rs`) admits exactly one external
//! dependency, `blake3`, so this module is the TerraScale-owned generator the
//! ADR's wrapper rule demands rather than an import of `rand` or of the
//! upstream crate's own `rand`-backed `Generator`.
//!
//! The stream is a BLAKE3 keyed counter-mode pseudorandom function: a
//! one-time seed is stretched into a 32-byte key with `blake3::derive_key`
//! under a fixed domain-separation context (BLAKE3's KDF mode, the same
//! construction `Hasher::new_derive_key` specifies for context-separated key
//! derivation), and block `i` of the stream is the keyed hash of `i` as a
//! big-endian 64-bit counter. Each draw takes the first ten bytes, so the
//! 80-bit entropy component is exactly one stream block prefix. The shape is
//! the counter mode of NIST SP 800-108 over a BLAKE3 PRF; the counter is
//! never reused because at wraparound the key itself is re-derived.
//!
//! Monotonicity is process-local and strict: within one millisecond the
//! 80-bit entropy component is incremented, and when it would overflow the
//! generator waits for a later millisecond rather than wrapping (never wrap,
//! never duplicate). Generator state survives neither as a correctness nor a
//! uniqueness dependency — fresh seed entropy and the full 128-bit value keep
//! identities independent across processes and Regions (ADR 0027).
//!
//! Honest limits, stated plainly. The engine core has no operating-system RNG
//! crate available, so the seed gathers entropy that is already inside the
//! process — `std`'s SipHash keys from `std::collections::hash_map::RandomState`
//! (which `std` seeds from the operating system RNG and which differ per
//! process and per construction), two wall-clock reads, the process and
//! thread identities, an address-space-randomized heap address, and a
//! process-local construction counter — and mixes them through BLAKE3's key
//! derivation. That is a documented engineering choice bounded by the
//! dependency rule, not a claim of a hardware RNG, and the seed material is
//! never reused across processes because every construction re-derives it.

// SipHash, RandomState, and TerraBase are proper names, not items to link.
#![allow(clippy::doc_markdown)]

use std::collections::hash_map::RandomState;
use std::hash::BuildHasher;
use std::sync::Arc;
use std::sync::Mutex;
use std::sync::OnceLock;
use std::sync::atomic::AtomicU64;
use std::sync::atomic::Ordering;
use std::time::Duration;
use std::time::SystemTime;
use std::time::UNIX_EPOCH;

use super::ulid::MAX_TIMESTAMP_MILLIS;
use super::ulid::Ulid;

/// The domain-separation context of the entropy stream: one fixed string per
/// purpose and version, so no other BLAKE3 use in the engine can ever produce
/// the same key stream.
const ENTROPY_CONTEXT: &str = "TerraBase/identity/ulid/generation/entropy/v1";

/// How long the generator sleeps between clock polls while it waits out an
/// entropy overflow. Short enough that the next millisecond is not missed by
/// more than a poll, long enough that the wait does not spin a core.
const OVERFLOW_POLL: Duration = Duration::from_micros(64);

/// The number of entropy bytes drawn per generated value: the full 80-bit
/// component, taken as the first ten bytes of one keyed-hash block.
const ENTROPY_BYTES: usize = 10;

/// Counts generator constructions so two streams built inside the same
/// nanosecond by the same thread still differ.
static CONSTRUCTIONS: AtomicU64 = AtomicU64::new(0);

/// The injectable millisecond clock. The default is the Unix wall clock; the
/// deterministic harnesses under `src/identity/tests.rs` inject a fixed or
/// stepping clock to prove the same-millisecond and overflow rules without
/// sleeping. Injecting a clock changes timing only — never the format, the
/// entropy source, or the monotonic rule.
pub type Clock = Arc<dyn Fn() -> u64 + Send + Sync>;

/// The BLAKE3 keyed counter-mode entropy stream.
struct EntropyStream {
    /// The domain-separated stream key.
    key: [u8; 32],
    /// The block counter, which is the only variable input per block.
    counter: u64,
}

impl EntropyStream {
    /// Derives a fresh stream from freshly gathered seed material.
    fn new() -> Self {
        Self { key: blake3::derive_key(ENTROPY_CONTEXT, &seed_material()), counter: 0 }
    }

    /// Draws the next 80-bit entropy component.
    fn next_entropy(&mut self) -> u128 {
        let block =
            blake3::Hasher::new_keyed(&self.key).update(&self.counter.to_be_bytes()).finalize();
        // Advance the counter; at wraparound re-derive the key so no block
        // index is ever hashed twice under one key.
        if let Some(next) = self.counter.checked_add(1) {
            self.counter = next;
        } else {
            self.key = blake3::derive_key(ENTROPY_CONTEXT, &self.key);
            self.counter = 0;
        }
        let mut entropy: u128 = 0;
        for byte in &block.as_bytes()[..ENTROPY_BYTES] {
            entropy = (entropy << 8) | u128::from(*byte);
        }
        entropy
    }
}

/// Gathers the process-local seed material described in the module header.
fn seed_material() -> Vec<u8> {
    let mut material = Vec::new();
    let construction = CONSTRUCTIONS.fetch_add(1, Ordering::Relaxed);
    material.extend_from_slice(&construction.to_be_bytes());
    material.extend_from_slice(&wall_clock_nanos().to_be_bytes());
    material.extend_from_slice(&u64::from(std::process::id()).to_be_bytes());
    material.extend_from_slice(&os_seeded_word().to_be_bytes());
    // Address-space randomization: the address of a fresh heap allocation
    // varies per process run without any unsafe code.
    let heap_probe = Box::new(0_u8);
    material.extend_from_slice(&(std::ptr::from_ref(&*heap_probe) as usize).to_be_bytes());
    drop(heap_probe);
    material.extend_from_slice(format!("{:?}", std::thread::current().id()).as_bytes());
    material
}

/// Returns one word of `std`'s operating-system-seeded hasher state. The
/// SipHash keys behind `RandomState` originate from the operating system RNG
/// and differ per process, which is the only operating-system entropy
/// reachable without adding a dependency to the engine core.
fn os_seeded_word() -> u64 {
    // Hashing a constant through the state is what exposes the keys: the
    // digest is a function of them and of nothing else in the process.
    RandomState::new().hash_one(0_u128)
}

/// Reads the Unix wall clock in nanoseconds, saturating at zero before the
/// epoch (a ULID timestamp is an unsigned component; a pre-1970 clock is not
/// representable and generation refuses to invent a negative one).
fn wall_clock_nanos() -> u128 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|elapsed| elapsed.as_nanos())
        .unwrap_or_default()
}

/// Reads the Unix wall clock in milliseconds, clamped into the representable
/// 48-bit component: a clock beyond `2^48 - 1` milliseconds (about 8.9
/// million years) saturates rather than refusing, and monotonicity inside the
/// saturated millisecond continues through the entropy component.
fn system_unix_millis() -> u64 {
    let millis = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|elapsed| u64::try_from(elapsed.as_millis()).unwrap_or(u64::MAX))
        .unwrap_or_default();
    millis.min(MAX_TIMESTAMP_MILLIS)
}

/// The mutable state one generator guards behind its mutex.
struct GeneratorState {
    /// The process-local entropy stream.
    stream: EntropyStream,
    /// The last value this generator emitted, which is what makes the
    /// sequence strictly increasing.
    previous: Option<Ulid>,
}

/// The outcome of one guarded step of generation.
enum Step {
    /// A value was emitted; the state already records it.
    Emitted(Ulid),
    /// The 80-bit entropy component is exhausted at this millisecond, so the
    /// caller must wait for a later one before trying again.
    WaitForLaterMillisecond(u64),
}

/// A cryptographically secure monotonic ULID generator (ADR 0027
/// 'Generation').
///
/// Within one process and millisecond the entropy component is incremented,
/// so every value a single generator emits is strictly greater than the one
/// before it, in the specification's chronological order. A clock that moves
/// backwards cannot break that promise: the generator keeps emitting inside
/// the last millisecond it used. Two generators in one process, or one
/// generator in two processes, are independent — each holds its own derived
/// stream, and neither relies on the other's state for uniqueness.
///
/// Application code reaches for [`process_generator`] (or the typed
/// `generate` constructors in [`crate::identity`]) rather than building its
/// own generator, so no caller selects an alternate random source or format
/// (ADR 0027 'Libraries and evidence').
pub struct MonotonicGenerator {
    state: Mutex<GeneratorState>,
    clock: Clock,
}

impl MonotonicGenerator {
    /// Builds a generator on the Unix wall clock with a freshly derived
    /// entropy stream.
    #[must_use]
    pub fn new() -> Self {
        Self::with_clock(Arc::new(system_unix_millis))
    }

    /// Builds a generator on an injected millisecond clock.
    ///
    /// This constructor exists for the deterministic corpus under
    /// `src/identity/tests.rs` — the same-millisecond and entropy-overflow
    /// rules need a fixed or stepping clock to be proven without sleeping.
    /// It changes timing only: the format, the entropy source, and the
    /// monotonic rule are identical, and no production path needs it.
    #[must_use]
    pub fn with_clock(clock: Clock) -> Self {
        Self {
            state: Mutex::new(GeneratorState { stream: EntropyStream::new(), previous: None }),
            clock,
        }
    }

    /// Locks the generator state.
    ///
    /// # Panics
    ///
    /// Panics if another thread panicked while holding the state lock: a
    /// poisoned generator has no trustworthy last value, and monotonicity
    /// must fail loudly rather than silently restart the sequence.
    fn lock(&self) -> std::sync::MutexGuard<'_, GeneratorState> {
        self.state.lock().expect("the identity generator lock is not poisoned")
    }

    /// Generates the next strictly increasing value.
    ///
    /// # Panics
    ///
    /// Panics if the generator lock is poisoned (see `Self::lock`). It
    /// cannot panic on the value itself: entropy drawn from the stream is
    /// always inside the 80-bit component.
    pub fn generate(&self) -> Ulid {
        loop {
            // The production clock is already clamped, but the injectable
            // clock is public for deterministic callers. Keep that seam
            // within ULID's representable timestamp range as well.
            let observed = (self.clock)().min(MAX_TIMESTAMP_MILLIS);
            let step = {
                let mut state = self.lock();
                let previous = state.previous;
                // Never move backwards, even if the clock does: the last used
                // millisecond is the floor of this draw.
                let millis = match previous {
                    Some(value) if value.timestamp_millis() > observed => value.timestamp_millis(),
                    _ => observed,
                };
                match previous {
                    Some(value) if millis == value.timestamp_millis() => {
                        match value.increment_entropy() {
                            Some(next) => {
                                state.previous = Some(next);
                                Step::Emitted(next)
                            }
                            // 2^80 - 1 values inside one millisecond: wait.
                            None => Step::WaitForLaterMillisecond(millis),
                        }
                    }
                    _ => {
                        let entropy = state.stream.next_entropy();
                        let value = Ulid::from_parts(millis, entropy).unwrap_or_else(|error| {
                            panic!("a drawn entropy component is always 80 bits: {error}")
                        });
                        state.previous = Some(value);
                        Step::Emitted(value)
                    }
                }
            };
            match step {
                Step::Emitted(value) => return value,
                Step::WaitForLaterMillisecond(millis) => self.wait_until_after(millis),
            }
        }
    }

    /// Waits until the injected clock reports a millisecond strictly later
    /// than `millis`. This is the ADR's entropy-overflow rule: the generator
    /// waits rather than wrapping, so no two values are ever equal and no
    /// value's timestamp component is ever silently advanced. Under a clock
    /// that never advances this wait does not return — the documented trade
    /// of refusing to wrap; the deterministic corpus exercises it with a
    /// clock that steps forward after a fixed number of polls.
    fn wait_until_after(&self, millis: u64) {
        while (self.clock)() <= millis {
            std::thread::sleep(OVERFLOW_POLL);
        }
    }

    /// Forces the last emitted value. Deterministic-corpus only: the
    /// entropy-overflow row needs a generator whose previous value already
    /// sits at [`MAX_ENTROPY`], which no real stream produces on demand.
    #[cfg(test)]
    pub(super) fn set_previous(&self, previous: Ulid) {
        self.lock().previous = Some(previous);
    }
}

impl Default for MonotonicGenerator {
    fn default() -> Self {
        Self::new()
    }
}

/// The process-wide generator. Every typed `generate` constructor in
/// [`crate::identity`] draws from this one instance, which is what makes the
/// whole process's identities strictly increasing within a millisecond.
pub fn process_generator() -> &'static MonotonicGenerator {
    static PROCESS_GENERATOR: OnceLock<MonotonicGenerator> = OnceLock::new();
    PROCESS_GENERATOR.get_or_init(MonotonicGenerator::new)
}

#[cfg(test)]
mod tests {
    use super::super::ulid::MAX_ENTROPY;
    use super::*;

    #[test]
    fn the_entropy_stream_never_repeats_a_block_under_one_key() {
        let mut stream = EntropyStream::new();
        let first = stream.next_entropy();
        let second = stream.next_entropy();
        assert_ne!(first, second);
        assert!(first <= MAX_ENTROPY);
        assert!(second <= MAX_ENTROPY);
    }

    #[test]
    fn two_streams_derive_independent_keys() {
        let mut first = EntropyStream::new();
        let mut second = EntropyStream::new();
        assert_ne!(first.next_entropy(), second.next_entropy());
    }
}
