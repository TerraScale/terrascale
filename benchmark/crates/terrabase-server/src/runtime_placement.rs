//! Journal-authorized reconstruction of immutable production member composition.

use crate::config::{ProductionConfig, ProductionPlacement};
use terrabase::identity::MemberUlid;
use terrabase_tbep::placement::{PlacementError, PlacementJournal};

/// Reads only the accepted signed chain. Newer staged intent has no effect.
pub(super) fn accepted_config(
    configured: &ProductionConfig,
    journal: &PlacementJournal,
) -> Result<ProductionConfig, PlacementError> {
    let accepted = journal.accepted_proposal()?;
    let proposal = accepted.proposal.ok_or(PlacementError::Invalid)?;
    project_config(configured, &proposal)
}

fn project_config(
    configured: &ProductionConfig,
    proposal: &terrabase_tbep::proto::terrabase::v1::PlacementProposal,
) -> Result<ProductionConfig, PlacementError> {
    if proposal.environment != configured.environment.name.as_str()
        || proposal.cluster != configured.environment.cluster.to_text()
        || proposal.generation < configured.placement.generation
        || proposal.epoch < configured.placement.epoch
    {
        return Err(PlacementError::Conflict);
    }
    let mut regions = std::collections::BTreeMap::new();
    let mut members = std::collections::BTreeSet::new();
    for seats in &proposal.regions {
        let region = seats.region.parse().map_err(|_| PlacementError::Invalid)?;
        let [first, second] = seats.members.as_slice() else {
            return Err(PlacementError::Invalid);
        };
        let first = MemberUlid::from_text(first).map_err(|_| PlacementError::Invalid)?;
        let second = MemberUlid::from_text(second).map_err(|_| PlacementError::Invalid)?;
        if !members.insert(first)
            || !members.insert(second)
            || regions.insert(region, [first, second]).is_some()
        {
            return Err(PlacementError::Invalid);
        }
    }
    if regions.is_empty() || !members.remove(&configured.environment.member) {
        return Err(PlacementError::Invalid);
    }
    let mut effective = configured.clone();
    // Endpoints and certificate identities are provisioned host configuration.
    // Signed member identifiers cannot invent a dial address or trust identity.
    effective.transport.peers = members
        .into_iter()
        .map(|member| {
            let mut matches =
                configured.transport.peers.iter().filter(|peer| peer.member == member);
            let peer = matches.next().ok_or(PlacementError::Invalid)?;
            if matches.next().is_some() {
                return Err(PlacementError::Invalid);
            }
            let identity =
                terrabase_cluster::production::MemberWorkloadIdentity::parse(&peer.spiffe_identity)
                    .map_err(|_| PlacementError::Invalid)?;
            let expected = terrabase_cluster::production::MemberWorkloadIdentity::try_new(
                configured.environment.name.as_str(),
                &configured.environment.cluster.to_text(),
                &member.to_text(),
            )
            .map_err(|_| PlacementError::Invalid)?;
            if identity != expected {
                return Err(PlacementError::Invalid);
            }
            Ok(peer.clone())
        })
        .collect::<Result<Vec<_>, _>>()?;
    effective.placement =
        ProductionPlacement { generation: proposal.generation, epoch: proposal.epoch, regions };
    Ok(effective)
}
