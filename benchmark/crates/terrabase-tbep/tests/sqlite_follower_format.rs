//! The `SQLite` WAL follower does not carry the retired storage format marker.

use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use terrabase_tbep::wal_follower::WalFollower;
use terrabase_tbep::{MemberHost, MemberHostOptions};

static NEXT_LOCATION: AtomicU64 = AtomicU64::new(1);

struct TestLocation(PathBuf);

impl TestLocation {
    fn new(label: &str) -> Self {
        let number = NEXT_LOCATION.fetch_add(1, Ordering::Relaxed);
        Self(std::env::temp_dir().join(format!(
            "terrabase-sqlite-follower-format-{label}-{}-{number}",
            std::process::id()
        )))
    }

    fn path(&self) -> &Path {
        &self.0
    }
}

impl Drop for TestLocation {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

/// Bootstrapping copies `SQLite`'s database state without reviving `format`.
#[test]
fn bootstrap_does_not_copy_retired_format_marker() {
    let writer_location = TestLocation::new("writer");
    let writer = MemberHost::open(
        writer_location.path(),
        7,
        "tenant-a",
        "project-a",
        "database-a",
        MemberHostOptions::tb1a(),
    )
    .expect("the writer opens");
    std::fs::write(writer_location.path().join("format"), b"legacy")
        .expect("the legacy marker is writable");

    let follower_location = TestLocation::new("follower");
    let (_follower, _checkpoint) =
        WalFollower::bootstrap(writer_location.path(), follower_location.path())
            .expect("the follower bootstraps without the marker");
    assert!(
        follower_location.path().join("terrabase.sqlite3").exists(),
        "the SQLite database is copied"
    );
    assert!(!follower_location.path().join("format").exists(), "the retired marker is not copied");

    drop(writer);
    MemberHost::open(
        follower_location.path(),
        7,
        "tenant-a",
        "project-a",
        "database-a",
        MemberHostOptions::tb1a(),
    )
    .expect("the marker-free follower promotes");
    assert!(
        !follower_location.path().join("format").exists(),
        "promotion does not recreate the retired marker"
    );
}
