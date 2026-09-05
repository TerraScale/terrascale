//! Ordered deltas shared by the incremental materializer and HTTP publisher.

use std::collections::{BTreeMap, BTreeSet};

use crate::wire::{ResultItem, ResultOperation};

/// Computes an ordered delta between validated, uniquely identified results.
///
/// Keeps a longest subsequence whose relative order did not change. Removed
/// or reordered rows are retracted first; inserts then use final positions
/// in result order. Unchanged rows emit nothing, and changed retained rows
/// emit field replacements. Reordered rows carry their new fields on insert.
/// Planning costs O((before + after) log(before + after)) and linear memory.
/// Both inputs must respect the result-size and identity bounds of a view.
#[must_use]
pub fn diff(before: &[ResultItem], after: &[ResultItem]) -> Vec<ResultOperation> {
    let old: BTreeMap<_, _> = before
        .iter()
        .enumerate()
        .map(|(position, item)| (item.identity.as_str(), (position, item)))
        .collect();
    let retained = retained_identities(&old, after);
    let mut operations = Vec::new();
    for item in before {
        if !retained.contains(item.identity.as_str()) {
            operations.push(ResultOperation::Remove { identity: item.identity.clone() });
        }
    }
    for (position, item) in after.iter().enumerate() {
        if !retained.contains(item.identity.as_str()) {
            operations.push(ResultOperation::Insert {
                identity: item.identity.clone(),
                position: u32::try_from(position).expect("validated view positions fit u32"),
                fields: item.fields.clone(),
            });
        } else if old[item.identity.as_str()].1.fields != item.fields {
            operations.push(ResultOperation::Update {
                identity: item.identity.clone(),
                fields: item.fields.clone(),
            });
        }
    }
    operations
}

fn retained_identities<'a>(
    old: &BTreeMap<&str, (usize, &ResultItem)>,
    after: &'a [ResultItem],
) -> BTreeSet<&'a str> {
    let mut tails = Vec::new();
    let mut tail_positions = Vec::new();
    let mut parents = vec![None; after.len()];
    for (position, item) in after.iter().enumerate() {
        let Some(&(old_position, _)) = old.get(item.identity.as_str()) else { continue };
        let length = tails.partition_point(|tail| *tail < old_position);
        if length > 0 {
            parents[position] = Some(tail_positions[length - 1]);
        }
        if length == tails.len() {
            tails.push(old_position);
            tail_positions.push(position);
        } else {
            tails[length] = old_position;
            tail_positions[length] = position;
        }
    }
    let mut retained = BTreeSet::new();
    let mut cursor = tail_positions.last().copied();
    while let Some(position) = cursor {
        retained.insert(after[position].identity.as_str());
        cursor = parents[position];
    }
    retained
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::wire::ProjectedField;

    fn item(identity: &str, value: u8) -> ResultItem {
        ResultItem {
            identity: identity.into(),
            fields: vec![ProjectedField { path: "value".into(), value: vec![b'0' + value] }],
        }
    }

    fn verify(before: &[ResultItem], after: &[ResultItem]) {
        let operations = diff(before, after);
        assert_eq!(operations, diff(before, after), "deterministic encoding input");
        let mut actual = before.to_vec();
        for operation in &operations {
            crate::view::apply_operation(&mut actual, operation)
                .expect("every intermediate operation is valid");
        }
        assert_eq!(actual, after, "operations: {operations:?}");
    }

    #[test]
    fn delta_handles_all_small_orderings_subsets_and_field_changes() {
        fn permutations(prefix: &[ResultItem], remaining: &[&str], out: &mut Vec<Vec<ResultItem>>) {
            out.push(prefix.to_vec());
            for (position, identity) in remaining.iter().enumerate() {
                let mut next = prefix.to_vec();
                next.push(item(identity, 0));
                let mut rest = remaining.to_vec();
                rest.remove(position);
                permutations(&next, &rest, out);
            }
        }
        let mut results = Vec::new();
        permutations(&[], &["a", "b", "c", "d"], &mut results);
        for before in &results {
            for after in &results {
                verify(before, after);
                for mask in 0..(1 << after.len()) {
                    let mut changed = after.clone();
                    for (position, row) in changed.iter_mut().enumerate() {
                        if mask & (1 << position) != 0 {
                            *row = item(&row.identity, 1);
                        }
                    }
                    verify(before, &changed);
                }
            }
        }
    }

    #[test]
    fn sparse_changes_do_not_resend_unchanged_rows() {
        let before: Vec<_> = (0..2_000).map(|i| item(&format!("row-{i}"), 0)).collect();
        assert!(diff(&before, &before).is_empty());
        let mut after = before.clone();
        after[800] = item("row-800", 1);
        assert_eq!(
            diff(&before, &after),
            vec![ResultOperation::Update {
                identity: "row-800".into(),
                fields: after[800].fields.clone(),
            }]
        );
        verify(&before, &after);
        after.rotate_right(1);
        assert_eq!(diff(&before, &after).len(), 3, "one relocation and one update");
        verify(&before, &after);
    }
}
