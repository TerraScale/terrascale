//! Expected lanes derived from the accepted proposal and verified assignments.
use std::collections::BTreeMap;

use terrabase_cluster::production::{DatabaseRouteScope, ProductionMemberRuntime};
use terrabase_cluster::wal_route::DurableWalAssignmentSource;
use terrabase_tbep::proto::terrabase::v1::PlacementProposal;

use crate::production_readiness::ExpectedScope;

pub(super) fn accepted_inventory(
    runtime: &ProductionMemberRuntime,
    source: &dyn DurableWalAssignmentSource,
    proposal: &PlacementProposal,
) -> Result<BTreeMap<DatabaseRouteScope, ExpectedScope>, String> {
    let transport = runtime.transport().config();
    if proposal.generation != transport.placement_generation()
        || proposal.epoch != transport.replica_set_epoch()
    {
        return Err("readiness inventory differs from the admitted epoch".into());
    }
    let local = transport.local_identity();
    let region = transport.local_region().as_str();
    let mut grouped = BTreeMap::<DatabaseRouteScope, Vec<_>>::new();
    for assignment in &proposal.assignments {
        let scope = DatabaseRouteScope::try_new(
            &assignment.tenant, &assignment.project, &assignment.database,
        ).map_err(super::failure)?;
        grouped.entry(scope).or_default().push(assignment);
    }
    let mut expected = BTreeMap::new();
    for (scope, assignments) in grouped {
        let local_assignments = assignments.iter().filter(|assignment| assignment.region == region)
            .collect::<Vec<_>>();
        let Some(selected) = local_assignments.first() else { continue; };
        if local_assignments.len() != 1 {
            return Err("readiness inventory has duplicate local assignments".into());
        }
        let verified = source.load_assignment(scope).map_err(super::failure)?;
        if verified.scope != scope || verified.generation != proposal.generation
            || verified.epoch != proposal.epoch
            || verified.writer.member().as_str() != selected.writer_member
            || verified.follower.member().as_str() != selected.follower_member
        {
            return Err("readiness inventory differs from verified assignment".into());
        }
        let local_is_writer = &verified.writer == local;
        if !local_is_writer && &verified.follower != local {
            return Err("readiness inventory has no accepted local seat".into());
        }
        let physical = if local_is_writer { &verified.follower } else { &verified.writer };
        let peer = transport.peer(physical.member()).ok_or("physical readiness peer missing")?;
        if peer.identity() != physical || peer.region().as_str() != region {
            return Err("physical readiness peer identity or Region mismatch".into());
        }
        let mut remote_writers = BTreeMap::new();
        for assignment in assignments {
            if assignment.region == region { continue; }
            let peer_id = terrabase_member_transport::identity::MemberId::try_new(
                &assignment.writer_member,
            ).map_err(super::failure)?;
            let peer = transport.peer(&peer_id).ok_or("remote readiness writer missing")?;
            if peer.region().as_str() != assignment.region
                || remote_writers.insert(assignment.region.clone(), assignment.writer_member.clone()).is_some()
            {
                return Err("remote readiness writer coverage differs from placement".into());
            }
        }
        expected.insert(scope, ExpectedScope {
            scope, generation: proposal.generation, epoch: proposal.epoch,
            local_region: region.to_owned(), local_is_writer,
            physical_peer: physical.member().as_str().to_owned(), remote_writers,
        });
    }
    Ok(expected)
}
