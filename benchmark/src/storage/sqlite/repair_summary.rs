//! Transactional ADR 0044 summaries. Versions make an old local journal cut exact.
use crate::{engine::ReplicatedPublication, types::TenantId};
use rusqlite::{Connection, OptionalExtension, params};
use std::collections::BTreeMap;
use terrabase_replication_core::anti_entropy::{JournalRowId, RangeFingerprint, row_contribution};

pub(super) const SCHEMA: &str = "
CREATE TABLE IF NOT EXISTS repair_identities (
 tenant TEXT NOT NULL, origin TEXT NOT NULL, source_seq INTEGER NOT NULL,
 local_seq INTEGER NOT NULL, fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
 PRIMARY KEY(tenant, origin, source_seq)
) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS repair_origins (
 tenant TEXT NOT NULL, origin TEXT NOT NULL, PRIMARY KEY(tenant, origin)
) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS repair_origin_versions (
 tenant TEXT NOT NULL, origin TEXT NOT NULL, local_seq INTEGER NOT NULL,
 source_hi INTEGER NOT NULL, PRIMARY KEY(tenant, origin, local_seq)
) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS repair_summary_versions (
 tenant TEXT NOT NULL, origin TEXT NOT NULL, level INTEGER NOT NULL,
 bucket INTEGER NOT NULL, local_seq INTEGER NOT NULL, row_count INTEGER NOT NULL,
 fingerprint BLOB NOT NULL CHECK(length(fingerprint)=16),
 PRIMARY KEY(tenant, origin, level, bucket, local_seq)
) WITHOUT ROWID;
";
const LEAF: u64 = 4096;
const LEVELS: u8 = 10;

fn contribution(origin: &str, sequence: u64, hash: &[u8; 32]) -> Result<u128, ()> {
    Ok(row_contribution(&JournalRowId::new(origin, sequence).map_err(|_| ())?, hash))
}

pub(super) fn persist(
    connection: &Connection,
    tenant: &TenantId,
    local_sequence: u64,
    publication: &ReplicatedPublication,
) -> Result<(), ()> {
    let hash = publication.fingerprint().map_err(|_| ())?;
    persist_hash(connection, tenant, &publication.origin, publication.origin_sequence, local_sequence, hash)
}

fn persist_hash(connection: &Connection, tenant: &TenantId, origin: &str, source: u64, local: u64, hash: [u8; 32]) -> Result<(), ()> {
    let source_i = i64::try_from(source).map_err(|_| ())?;
    let local_i = i64::try_from(local).map_err(|_| ())?;
    if source == 0 || local == 0 { return Err(()); }
    let previous: Option<Vec<u8>> = connection.query_row(
        "SELECT fingerprint FROM repair_identities WHERE tenant=?1 AND origin=?2 AND source_seq=?3",
        params![tenant.as_str(), origin, source_i], |row| row.get(0)
    ).optional().map_err(|_| ())?;
    if let Some(previous) = previous { return if previous == hash { Ok(()) } else { Err(()) }; }
    connection.execute("INSERT INTO repair_identities VALUES (?1,?2,?3,?4,?5)", params![tenant.as_str(),origin,source_i,local_i,hash.as_slice()]).map_err(|_| ())?;
    connection.execute("INSERT OR IGNORE INTO repair_origins VALUES (?1,?2)", params![tenant.as_str(),origin]).map_err(|_| ())?;
    let hi: i64 = connection.query_row("SELECT COALESCE((SELECT source_hi FROM repair_origin_versions WHERE tenant=?1 AND origin=?2 ORDER BY local_seq DESC LIMIT 1),0)", params![tenant.as_str(),origin], |row| row.get(0)).map_err(|_| ())?;
    connection.execute("INSERT INTO repair_origin_versions VALUES (?1,?2,?3,?4)", params![tenant.as_str(),origin,local_i,hi.max(source_i)]).map_err(|_| ())?;
    let contribution = contribution(origin, source, &hash)?;
    let mut bucket = (source - 1) / LEAF;
    for level in 0..LEVELS {
        let old = bucket_summary(connection, tenant, origin, level, bucket, u64::try_from(local_i).map_err(|_| ())?)?;
        let count = i64::try_from(old.count.checked_add(1).ok_or(())?).map_err(|_| ())?;
        let sum = old.sum.wrapping_add(contribution).to_be_bytes();
        connection.execute("INSERT INTO repair_summary_versions VALUES (?1,?2,?3,?4,?5,?6,?7)", params![tenant.as_str(),origin,level,i64::try_from(bucket).map_err(|_| ())?,local_i,count,sum.as_slice()]).map_err(|_| ())?;
        bucket /= 64;
    }
    Ok(())
}

fn bucket_summary(connection: &Connection, tenant: &TenantId, origin: &str, level: u8, bucket: u64, cut: u64) -> Result<RangeFingerprint, ()> {
    connection.query_row("SELECT row_count,fingerprint FROM repair_summary_versions WHERE tenant=?1 AND origin=?2 AND level=?3 AND bucket=?4 AND local_seq<=?5 ORDER BY local_seq DESC LIMIT 1", params![tenant.as_str(),origin,level,i64::try_from(bucket).map_err(|_| ())?,i64::try_from(cut).map_err(|_| ())?], |row| {
        let count: i64 = row.get(0)?;
        let hash: Vec<u8> = row.get(1)?;
        Ok((count,hash))
    }).optional().map_err(|_| ())?.map_or(Ok(RangeFingerprint::default()), |(count, hash)| Ok(RangeFingerprint { count: u64::try_from(count).map_err(|_| ())?, sum: u128::from_be_bytes(hash.try_into().map_err(|_| ())?) }))
}

pub(super) fn frontier(connection: &Connection, tenant: &TenantId, cut: u64) -> Result<BTreeMap<String,u64>, ()> {
    let mut statement = connection.prepare("SELECT origin,(SELECT source_hi FROM repair_origin_versions v WHERE v.tenant=o.tenant AND v.origin=o.origin AND local_seq<=?2 ORDER BY local_seq DESC LIMIT 1) FROM repair_origins o WHERE tenant=?1 ORDER BY origin LIMIT 257").map_err(|_| ())?;
    let mut rows = statement.query(params![tenant.as_str(),i64::try_from(cut).map_err(|_| ())?]).map_err(|_| ())?;
    let mut result = BTreeMap::new();
    while let Some(row) = rows.next().map_err(|_| ())? {
        if let Some(hi) = row.get::<_,Option<i64>>(1).map_err(|_| ())? {
            result.insert(row.get(0).map_err(|_| ())?, u64::try_from(hi).map_err(|_| ())?);
        }
        if result.len() > 256 { return Err(()); }
    }
    Ok(result)
}

pub(super) fn summary(connection: &Connection, tenant: &TenantId, origin: &str, cut: u64, first: u64, last: u64) -> Result<RangeFingerprint, ()> {
    if first == 0 || first > last || last > i64::MAX as u64 { return Err(()); }
    let mut result = RangeFingerprint::default();
    let mut position = first - 1;
    while position < last {
        let remaining = last - position;
        if position % LEAF != 0 || remaining < LEAF {
            let end = last.min((position / LEAF + 1) * LEAF);
            for (_, _, hash) in identities(connection, tenant, origin, cut, position + 1, end)? {
                result.count = result.count.checked_add(1).ok_or(())?;
                // Identity queries return the stable source sequence below.
                result.sum = result.sum.wrapping_add(hash);
            }
            position = end;
            continue;
        }
        let mut span = LEAF;
        let mut level = 0;
        while level + 1 < LEVELS {
            let Some(next) = span.checked_mul(64) else { break; };
            if next > remaining || position % next != 0 { break; }
            span = next;
            level += 1;
        }
        let part = bucket_summary(connection, tenant, origin, level, position / span, cut)?;
        result.count = result.count.checked_add(part.count).ok_or(())?;
        result.sum = result.sum.wrapping_add(part.sum);
        position += span;
    }
    Ok(result)
}

fn identities(connection: &Connection, tenant: &TenantId, origin: &str, cut: u64, first: u64, last: u64) -> Result<Vec<(u64,[u8;32],u128)>, ()> {
    if first == 0 || last < first || last - first >= LEAF { return Err(()); }
    let mut statement = connection.prepare("SELECT source_seq,fingerprint FROM repair_identities WHERE tenant=?1 AND origin=?2 AND source_seq>=?3 AND source_seq<=?4 AND local_seq<=?5 ORDER BY source_seq").map_err(|_| ())?;
    let mut rows = statement.query(params![tenant.as_str(),origin,i64::try_from(first).map_err(|_| ())?,i64::try_from(last).map_err(|_| ())?,i64::try_from(cut).map_err(|_| ())?]).map_err(|_| ())?;
    let mut result = Vec::new();
    while let Some(row) = rows.next().map_err(|_| ())? {
        let sequence = u64::try_from(row.get::<_,i64>(0).map_err(|_| ())?).map_err(|_| ())?;
        let hash: [u8;32] = row.get::<_,Vec<u8>>(1).map_err(|_| ())?.try_into().map_err(|_| ())?;
        result.push((sequence,hash,contribution(origin, sequence, &hash)?));
    }
    Ok(result)
}

pub(super) fn publications(connection: &Connection, tenant: &TenantId, origin: &str, cut: u64, first: u64, last: u64) -> Result<Vec<ReplicatedPublication>, ()> {
    let identities = identities(connection, tenant, origin, cut, first, last)?;
    let mut result = Vec::with_capacity(identities.len());
    let mut bytes = 0_usize;
    for (sequence, expected, _) in identities {
        let local: i64 = connection.query_row("SELECT local_seq FROM replication_publications WHERE tenant=?1 AND origin=?2 AND origin_seq=?3 AND local_seq<=?4", params![tenant.as_str(),origin,i64::try_from(sequence).map_err(|_| ())?,i64::try_from(cut).map_err(|_| ())?], |row| row.get(0)).map_err(|_| ())?;
        let mut rows = super::replication_outbox::read_publications(connection,tenant,u64::try_from(local-1).map_err(|_| ())?,1)?;
        let (actual_local, publication) = rows.pop().ok_or(())?;
        if actual_local != local as u64 || publication.origin != origin || publication.origin_sequence != sequence || publication.fingerprint().map_err(|_| ())? != expected { return Err(()); }
        // Leaf materialization is byte bounded across all source groups.
        bytes = bytes.checked_add(publication.private_payload.len()+publication.context.len()).ok_or(())?;
        for record in &publication.records {
            bytes = bytes.checked_add(crate::storage::codec::encode_private_value(&record.body).ok_or(())?.len()).ok_or(())?;
        }
        if bytes > 64*1024*1024 { return Err(()); }
        result.push(publication);
    }
    Ok(result)
}

#[cfg(test)]
mod tests {
    use super::*;
    fn setup() -> (Connection, TenantId) {
        let connection = Connection::open_in_memory().unwrap();
        connection.execute_batch(SCHEMA).unwrap();
        (connection,TenantId::try_new("tenant").unwrap())
    }
    #[test]
    fn summaries_split_at_leaf_and_parent_boundaries_and_freeze_out_of_order_arrivals() {
        let (mut connection, tenant) = setup();
        let source_positions = [1,4095,4096,4097,262144,262145,1_000_000_000];
        for (index, source) in source_positions.iter().enumerate() {
            let tx = connection.transaction().unwrap();
            persist_hash(&tx,&tenant,"gru",*source,index as u64+1,[index as u8;32]).unwrap();
            tx.commit().unwrap();
        }
        let old = summary(&connection,&tenant,"gru",7,1,1_000_000_000).unwrap();
        assert_eq!(old.count,7);
        for split in [1,4095,4096,4097,262144,262145,900_000_000] {
            let left = summary(&connection,&tenant,"gru",7,1,split).unwrap();
            let right = summary(&connection,&tenant,"gru",7,split+1,1_000_000_000).unwrap();
            assert_eq!(left.count+right.count,old.count);
            assert_eq!(left.sum.wrapping_add(right.sum),old.sum);
        }
        let tx = connection.transaction().unwrap();
        persist_hash(&tx,&tenant,"gru",2,8,[19;32]).unwrap();
        tx.commit().unwrap();
        assert_eq!(summary(&connection,&tenant,"gru",7,1,1_000_000_000).unwrap(),old);
        assert_eq!(summary(&connection,&tenant,"gru",8,1,1_000_000_000).unwrap().count,8);
        assert_eq!(summary(&connection,&tenant,"sfo",8,1,1_000_000_000).unwrap().count,0);
        assert_eq!(summary(&connection,&TenantId::try_new("other").unwrap(),"gru",8,1,1_000_000_000).unwrap().count,0);
        assert_eq!(frontier(&connection,&tenant,7).unwrap()["gru"],1_000_000_000);
        let versions: i64 = connection.query_row("SELECT COUNT(*) FROM repair_summary_versions",[],|row| row.get(0)).unwrap();
        assert_eq!(versions,8*i64::from(LEVELS));
    }
    #[test]
    fn replay_identity_and_rollback_do_not_change_any_aggregate() {
        let (mut connection,tenant) = setup();
        let tx = connection.transaction().unwrap();
        persist_hash(&tx,&tenant,"gru",1,1,[1;32]).unwrap();
        tx.commit().unwrap();
        let baseline = summary(&connection,&tenant,"gru",9,1,4096).unwrap();
        let tx = connection.transaction().unwrap();
        persist_hash(&tx,&tenant,"gru",1,2,[1;32]).unwrap();
        assert!(persist_hash(&tx,&tenant,"gru",1,3,[2;32]).is_err());
        persist_hash(&tx,&tenant,"gru",2,4,[3;32]).unwrap();
        tx.rollback().unwrap();
        assert_eq!(summary(&connection,&tenant,"gru",9,1,4096).unwrap(),baseline);
        assert_eq!(frontier(&connection,&tenant,9).unwrap()["gru"],1);
    }
    #[test]
    fn summary_queries_use_primary_key_ranges() {
        let (connection, _) = setup();
        let mut query = connection.prepare("EXPLAIN QUERY PLAN SELECT row_count,fingerprint FROM repair_summary_versions WHERE tenant='t' AND origin='o' AND level=0 AND bucket=0 AND local_seq<=9 ORDER BY local_seq DESC LIMIT 1").unwrap();
        let detail: String = query.query_row([],|row| row.get(3)).unwrap();
        assert!(detail.contains("SEARCH repair_summary_versions USING PRIMARY KEY"),"{detail}");
        assert!(!detail.contains("TEMP"));
    }
}
