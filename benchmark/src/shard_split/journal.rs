//! Journal catch-up module facade.

#[path = "catch_up.rs"]
mod implementation;

pub use implementation::{
    CatchUpError, JournalCatchUp, JournalEntry, JournalOperation, JournalReader, JournalWriter,
};
