//! Process-shared membership authority lease. Delivery performs local lookups only.
use serde::Deserialize;
use std::sync::RwLock;

#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub(crate) struct Entry {
    pub scope: String,
    pub expires_at: u64,
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub(crate) struct Snapshot {
    pub revision: String,
    pub valid_until: u64,
    pub entries: Vec<Entry>,
}
#[derive(Default)]
pub(crate) struct MembershipRevocations {
    snapshot: RwLock<Option<Snapshot>>,
}

pub(crate) fn valid_scope(scope: &str) -> bool {
    scope.len() == 70
        && scope.starts_with("scope-")
        && scope.as_bytes()[6..].iter().all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(c))
}
impl MembershipRevocations {
    pub fn install(&self, next: Snapshot, now: u64) -> Result<(), ()> {
        if next.entries.len() > 32
            || next.valid_until <= now
            || next.valid_until > now.saturating_add(30)
        {
            return Err(());
        }
        let mut canonical = String::new();
        let mut previous = "";
        for entry in &next.entries {
            if !valid_scope(&entry.scope)
                || entry.scope.as_str() <= previous
                || entry.expires_at > 9_007_199_254_740_991
            {
                return Err(());
            }
            use std::fmt::Write as _;
            let _ = writeln!(canonical, "{}.{}", entry.scope, entry.expires_at);
            previous = &entry.scope;
        }
        let digest = aws_lc_rs::digest::digest(&aws_lc_rs::digest::SHA256, canonical.as_bytes());
        if next.revision != hex::encode(digest.as_ref()) {
            return Err(());
        }
        let mut current = self.snapshot.write().map_err(|_| ())?;
        if let Some(old) = current.as_ref() {
            // A replay or rollback cannot refresh authority or resurrect an old scope.
            if next.valid_until < old.valid_until
                || old.entries.iter().any(|entry| {
                    entry.expires_at > now
                        && !next.entries.iter().any(|new| {
                            new.scope == entry.scope && new.expires_at >= entry.expires_at
                        })
                })
            {
                return Err(());
            }
        }
        *current = Some(next);
        Ok(())
    }
    pub fn admits(&self, scope: &str, now: u64) -> bool {
        valid_scope(scope)
            && self.snapshot.read().ok().is_some_and(|snapshot| {
                snapshot.as_ref().is_some_and(|snapshot| {
                    snapshot.valid_until > now
                        && !snapshot
                            .entries
                            .iter()
                            .any(|entry| entry.scope == scope && entry.expires_at > now)
                })
            })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn snapshot(now: u64, scopes: &[(&str, u64)]) -> Snapshot {
        let canonical =
            scopes.iter().map(|(scope, expiry)| format!("{scope}.{expiry}\n")).collect::<String>();
        Snapshot {
            valid_until: now + 30,
            revision: hex::encode(
                aws_lc_rs::digest::digest(&aws_lc_rs::digest::SHA256, canonical.as_bytes())
                    .as_ref(),
            ),
            entries: scopes
                .iter()
                .map(|(scope, expiry)| Entry { scope: (*scope).into(), expires_at: *expiry })
                .collect(),
        }
    }
    #[test]
    fn membership_snapshot_never_resurrects_and_expiry_fails_closed() {
        let scope = format!("scope-{}", "a".repeat(64));
        let other = format!("scope-{}", "b".repeat(64));
        let registry = MembershipRevocations::default();
        assert!(!registry.admits(&other, 100));
        registry.install(snapshot(100, &[]), 100).unwrap();
        assert!(registry.admits(&scope, 100));
        registry.install(snapshot(101, &[(&scope, 1420)]), 101).unwrap();
        assert!(!registry.admits(&scope, 101));
        assert!(registry.admits(&other, 101));
        assert!(registry.install(snapshot(102, &[]), 102).is_err());
        assert!(!registry.admits(&other, 131));
        registry.install(snapshot(1420, &[]), 1420).unwrap();
        assert!(registry.admits(&other, 1420));
    }
}
