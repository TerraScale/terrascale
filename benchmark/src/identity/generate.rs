//! Shared generation using the `ulid` crate's OS-seeded monotonic generator.

use std::sync::Mutex;
use std::time::Duration;

static GENERATOR: Mutex<::ulid::Generator> = Mutex::new(::ulid::Generator::new());

/// Generates a process-wide monotonic ULID using the crate's cryptographic RNG.
/// Entropy overflow retries until the clock advances, without holding the lock.
///
/// # Panics
///
/// Panics if the generator lock is poisoned or the OS-seeded RNG cannot initialize.
pub fn generate() -> ::ulid::Ulid {
    loop {
        let result =
            GENERATOR.lock().expect("the identity generator lock is not poisoned").generate();
        match result {
            Ok(value) => return value,
            Err(::ulid::MonotonicError::Overflow) => {
                std::thread::sleep(Duration::from_micros(64));
            }
        }
    }
}
