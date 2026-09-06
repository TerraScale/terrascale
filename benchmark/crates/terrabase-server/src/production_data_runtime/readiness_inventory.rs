//! Expected lanes derived from the accepted proposal and verified assignments.
use std::collections::BTreeMap;

use terrabase_cluster::production::{DatabaseRouteScope, ProductionMemberRuntime};
use terrabase_cluster::wal_route::DurableWalAssignmentSource;
use terrabase_tbep::proto::terrabase::v1::PlacementProposal;

use crate::production_readiness::ExpectedScope;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum InventoryError {
    Unavailable,
    Invalid,
}

pub(super) fn accepted_inventory(
    runtime: &ProductionMemberRuntime,
    source: &dyn DurableWalAssignmentSource,
    proposal: &PlacementProposal,
) -> Result<BTreeMap<DatabaseRouteScope, ExpectedScope>, InventoryError> {
    let transport = runtime.transport().config();
    if proposal.generation != transport.placement_generation()
        || proposal.epoch != transport.replica_set_epoch()
    {
        return Err(InventoryError::Invalid);
    }
    let local = transport.local_identity();
    let region = transport.local_region().as_str();
    let mut grouped = BTreeMap::<DatabaseRouteScope, Vec<_>>::new();
    for assignment in &proposal.assignments {
        let scope = DatabaseRouteScope::try_new(
            &assignment.tenant,
            &assignment.project,
            &assignment.database,
        )
        .map_err(|_| InventoryError::Invalid)?;
        grouped.entry(scope).or_default().push(assignment);
    }
    let mut expected = BTreeMap::new();
    for (scope, assignments) in grouped {
        let local_assignments =
            assignments.iter().filter(|assignment| assignment.region == region).collect::<Vec<_>>();
        let Some(selected) = local_assignments.first() else {
            continue;
        };
        if local_assignments.len() != 1 {
            return Err(InventoryError::Invalid);
        }
        let verified = source.load_assignment(scope).map_err(|error| match error {
            terrabase_cluster::wal_route::WalAssignmentError::Unavailable => {
                InventoryError::Unavailable
            }
            terrabase_cluster::wal_route::WalAssignmentError::InvalidProof => {
                InventoryError::Invalid
            }
        })?;
        if verified.scope != scope
            || verified.generation != proposal.generation
            || verified.epoch != proposal.epoch
            || verified.writer.member().as_str() != selected.writer_member
            || verified.follower.member().as_str() != selected.follower_member
        {
            return Err(InventoryError::Invalid);
        }
        let local_is_writer = &verified.writer == local;
        if !local_is_writer && &verified.follower != local {
            return Err(InventoryError::Invalid);
        }
        let physical = if local_is_writer { &verified.follower } else { &verified.writer };
        let peer = transport.peer(physical.member()).ok_or(InventoryError::Invalid)?;
        if peer.identity() != physical || peer.region().as_str() != region {
            return Err(InventoryError::Invalid);
        }
        let mut remote_writers = BTreeMap::new();
        for assignment in assignments {
            if assignment.region == region {
                continue;
            }
            let peer_id =
                terrabase_member_transport::identity::MemberId::try_new(&assignment.writer_member)
                    .map_err(|_| InventoryError::Invalid)?;
            let peer = transport.peer(&peer_id).ok_or(InventoryError::Invalid)?;
            if peer.region().as_str() != assignment.region
                || remote_writers
                    .insert(assignment.region.clone(), assignment.writer_member.clone())
                    .is_some()
            {
                return Err(InventoryError::Invalid);
            }
        }
        expected.insert(
            scope,
            ExpectedScope {
                scope,
                generation: proposal.generation,
                epoch: proposal.epoch,
                local_region: region.to_owned(),
                local_is_writer,
                physical_peer: physical.member().as_str().to_owned(),
                remote_writers,
            },
        );
    }
    Ok(expected)
}

#[cfg(test)]
mod tests {
    use super::*;
    use terrabase_cluster::wal_route::{WalAssignment, WalAssignmentError};
    use terrabase_tbep::proto::terrabase::v1::PlacementDatabaseAssignment;

    struct Accepted(WalAssignment);
    impl DurableWalAssignmentSource for Accepted {
        fn load_assignment(
            &self,
            scope: DatabaseRouteScope,
        ) -> Result<WalAssignment, WalAssignmentError> {
            if self.0.scope != scope {
                return Err(WalAssignmentError::InvalidProof);
            }
            Ok(self.0.clone())
        }
    }

    #[tokio::test]
    async fn inventory_requires_verified_local_roles_and_exact_epoch() {
        let (runtime, root, spec) =
            crate::production::tests::runtime_for_readiness("readiness-inventory");
        let owner = spec.scope().unwrap();
        let scope = DatabaseRouteScope::new(owner.tenant(), owner.project(), owner.database());
        let transport = runtime.transport().config();
        let local = transport.local_identity().clone();
        let peer = transport.peers().next().unwrap().identity().clone();
        let accepted = Accepted(WalAssignment {
            scope,
            generation: transport.placement_generation(),
            epoch: transport.replica_set_epoch(),
            writer: local.clone(),
            follower: peer.clone(),
        });
        let mut proposal = PlacementProposal {
            generation: accepted.0.generation,
            epoch: accepted.0.epoch,
            assignments: vec![PlacementDatabaseAssignment {
                tenant: scope.tenant().to_text(),
                project: scope.project().to_text(),
                database: scope.database().to_text(),
                region: transport.local_region().as_str().to_owned(),
                writer_member: local.member().as_str().to_owned(),
                follower_member: peer.member().as_str().to_owned(),
            }],
            ..Default::default()
        };
        let inventory = accepted_inventory(&runtime, &accepted, &proposal).unwrap();
        assert_eq!(inventory.len(), 1);
        assert!(inventory[&scope].local_is_writer);
        assert_eq!(inventory[&scope].physical_peer, peer.member().as_str());
        proposal.epoch += 1;
        assert!(accepted_inventory(&runtime, &accepted, &proposal).is_err());
        proposal.epoch -= 1;
        let row = &mut proposal.assignments[0];
        std::mem::swap(&mut row.writer_member, &mut row.follower_member);
        assert!(accepted_inventory(&runtime, &accepted, &proposal).is_err());
        let follower =
            Accepted(WalAssignment { writer: peer.clone(), follower: local, ..accepted.0 });
        let inventory = accepted_inventory(&runtime, &follower, &proposal).unwrap();
        assert!(!inventory[&scope].local_is_writer);
        assert_eq!(inventory[&scope].physical_peer, peer.member().as_str());
        let duplicate = proposal.assignments[0].clone();
        proposal.assignments.push(duplicate);
        assert!(accepted_inventory(&runtime, &follower, &proposal).is_err());
        runtime.drain().await.storage.unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }
}
