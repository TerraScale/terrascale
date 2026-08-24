//! Private storage declarations reserved for the W3-1 recovery lane.

#![allow(dead_code)]

use std::marker::PhantomData;

/// The internal storage adapter seam.
pub(crate) trait StoragePort {}

struct WalRecord(PhantomData<()>);
struct WalFrame(PhantomData<()>);
struct WalOffset(PhantomData<()>);
struct CommitMarker(PhantomData<()>);
struct StorageKey(PhantomData<()>);
struct EncodedDocument(PhantomData<()>);
struct EncodedValue(PhantomData<()>);
struct FileStorage(PhantomData<()>);
struct FixtureStorage(PhantomData<()>);
struct Manifest(PhantomData<()>);
struct StorageRoot(PhantomData<()>);
struct RecoveryState(PhantomData<()>);
struct CommitIntent(PhantomData<()>);
struct PreparedCommit(PhantomData<()>);
struct StorageReceipt(PhantomData<()>);
struct OperationLedgerRecord(PhantomData<()>);
struct ReadyToPublish(PhantomData<()>);
struct Published(PhantomData<()>);
