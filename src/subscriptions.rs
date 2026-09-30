//! Native cursor-based snapshots and ordered atomic change groups.

use crate::client::SharedClient;
use crate::discovery::NativeRequirement;
use crate::documents::{
    validate_cursor, validate_limit, validate_rows, DatabaseScope, Point, PointState, Predicate,
    ProtocolVersion, QueryRow,
};
use crate::error::{Error, Result};
use crate::transport::{Method, Request};
use crate::values::{invalid, CollectionId, DecimalU64, DocumentId};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum SnapshotPhase {
    #[serde(rename = "snapshot")]
    Snapshot,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum PollPhase {
    #[serde(rename = "poll")]
    Poll,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct SnapshotRequest {
    pub version: ProtocolVersion,
    pub phase: SnapshotPhase,
    pub predicate: Predicate,
    pub limit: u16,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub snapshot_cursor: Option<String>,
}

impl SnapshotRequest {
    pub fn new(predicate: Predicate, limit: u16) -> Result<Self> {
        predicate.validate()?;
        validate_limit(limit)?;
        Ok(Self {
            version: ProtocolVersion,
            phase: SnapshotPhase::Snapshot,
            predicate,
            limit,
            snapshot_cursor: None,
        })
    }
    fn validate(&self) -> Result<()> {
        self.predicate.validate()?;
        validate_limit(self.limit)?;
        if let Some(cursor) = &self.snapshot_cursor {
            validate_cursor(cursor)?;
        }
        Ok(())
    }
}

fn present_cursor<'de, D: serde::Deserializer<'de>>(
    d: D,
) -> std::result::Result<Option<String>, D::Error> {
    String::deserialize(d).map(Some)
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct SnapshotPage {
    pub version: ProtocolVersion,
    pub phase: SnapshotPhase,
    pub rows: Vec<QueryRow>,
    #[serde(
        default,
        deserialize_with = "present_cursor",
        skip_serializing_if = "Option::is_none"
    )]
    pub next_snapshot_cursor: Option<String>,
    #[serde(
        default,
        deserialize_with = "present_cursor",
        skip_serializing_if = "Option::is_none"
    )]
    pub poll_cursor: Option<String>,
}

impl SnapshotPage {
    pub fn validate_in(&self, scope: &DatabaseScope, limit: u16) -> Result<()> {
        validate_rows(&self.rows, scope, usize::from(limit))?;
        match (&self.next_snapshot_cursor, &self.poll_cursor) {
            (Some(cursor), None) | (None, Some(cursor)) => validate_cursor(cursor),
            _ => Err(invalid(
                "Snapshot page must contain exactly one continuation kind.",
            )),
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct PollRequest {
    pub version: ProtocolVersion,
    pub phase: PollPhase,
    pub predicate: Predicate,
    pub poll_cursor: String,
}

impl PollRequest {
    pub fn new(predicate: Predicate, poll_cursor: String) -> Result<Self> {
        predicate.validate()?;
        validate_cursor(&poll_cursor)?;
        Ok(Self {
            version: ProtocolVersion,
            phase: PollPhase::Poll,
            predicate,
            poll_cursor,
        })
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct SubscriptionEffect {
    pub document: DocumentId,
    /// `None` removes a document from the view, not an absent point state.
    #[serde(deserialize_with = "required_point")]
    pub point: Option<Point>,
}

fn required_point<'de, D: serde::Deserializer<'de>>(
    d: D,
) -> std::result::Result<Option<Point>, D::Error> {
    Option::<Point>::deserialize(d)
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct SubscriptionGroup {
    pub sequence: DecimalU64,
    pub effects: Vec<SubscriptionEffect>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct PollPage {
    pub version: ProtocolVersion,
    pub phase: PollPhase,
    pub groups: Vec<SubscriptionGroup>,
    pub poll_cursor: String,
}

impl PollPage {
    pub fn validate_in(&self, scope: &DatabaseScope) -> Result<()> {
        validate_cursor(&self.poll_cursor)?;
        if self.groups.len() > 8 {
            return Err(invalid("Poll page exceeds eight complete groups."));
        }
        let mut previous_sequence = 0;
        for group in &self.groups {
            if group.sequence.0 <= previous_sequence || group.effects.len() > 128 {
                return Err(invalid(
                    "Poll groups need increasing positive sequences and at most 128 effects.",
                ));
            }
            previous_sequence = group.sequence.0;
            let mut previous_document = None;
            for effect in &group.effects {
                if previous_document.is_some_and(|p| p >= effect.document) {
                    return Err(invalid("Group effects must have ordered unique documents."));
                }
                previous_document = Some(effect.document);
                if let Some(point) = &effect.point {
                    point.validate_in(scope)?;
                    if !matches!(point.state, PointState::Value | PointState::Conflict) {
                        return Err(invalid(
                            "Subscription view must preserve live or conflicted points.",
                        ));
                    }
                }
            }
        }
        Ok(())
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
struct ViewBinding {
    scope: DatabaseScope,
    collection: CollectionId,
    predicate: Predicate,
}

#[derive(Clone, Debug, Default)]
enum ViewPhase {
    #[default]
    Empty,
    Snapshot(String),
    Poll(String),
}

/// A view bound to one scoped collection and predicate with validated cursor transitions.
#[derive(Clone, Debug, Default)]
pub struct SubscriptionView {
    binding: Option<ViewBinding>,
    phase: ViewPhase,
    rows: BTreeMap<DocumentId, Point>,
    sequence: Option<DecimalU64>,
}

impl SubscriptionView {
    fn validate_binding(
        &self,
        scope: &DatabaseScope,
        collection: CollectionId,
        predicate: &Predicate,
    ) -> Result<()> {
        let binding = ViewBinding {
            scope: *scope,
            collection,
            predicate: predicate.clone(),
        };
        if self.binding.as_ref().is_some_and(|bound| bound != &binding) {
            return Err(invalid("Subscription view cannot cross its tenant/database/shard, collection or predicate."));
        }
        Ok(())
    }
    #[must_use]
    pub fn rows(&self) -> &BTreeMap<DocumentId, Point> {
        &self.rows
    }
    #[must_use]
    pub fn sequence(&self) -> Option<DecimalU64> {
        self.sequence
    }
    #[must_use]
    pub fn snapshot_cursor(&self) -> Option<&str> {
        match &self.phase {
            ViewPhase::Snapshot(cursor) => Some(cursor),
            _ => None,
        }
    }
    #[must_use]
    pub fn poll_cursor(&self) -> Option<&str> {
        match &self.phase {
            ViewPhase::Poll(cursor) => Some(cursor),
            _ => None,
        }
    }

    pub fn apply_snapshot(
        &mut self,
        scope: &DatabaseScope,
        collection: CollectionId,
        input: &SnapshotRequest,
        page: &SnapshotPage,
    ) -> Result<()> {
        input.validate()?;
        self.validate_binding(scope, collection, &input.predicate)?;
        match (&self.phase, input.snapshot_cursor.as_deref()) {
            (ViewPhase::Empty, None) => {}
            (ViewPhase::Snapshot(expected), Some(actual)) if expected == actual => {}
            _ => {
                return Err(invalid(
                    "Snapshot request does not continue this view's current snapshot phase/cursor.",
                ))
            }
        }
        page.validate_in(scope, input.limit)?;
        if let (Some((last, _)), Some(first)) = (self.rows.last_key_value(), page.rows.first()) {
            if first.document <= *last {
                return Err(invalid(
                    "Snapshot documents must remain ordered across pages.",
                ));
            }
        }
        let mut rows = self.rows.clone();
        for row in &page.rows {
            if rows.insert(row.document, row.point.clone()).is_some() {
                return Err(invalid("Snapshot document repeated across pages."));
            }
        }
        let phase = match (&page.next_snapshot_cursor, &page.poll_cursor) {
            (Some(cursor), None) => ViewPhase::Snapshot(cursor.clone()),
            (None, Some(cursor)) => ViewPhase::Poll(cursor.clone()),
            _ => return Err(invalid("Snapshot continuation is invalid.")),
        };
        self.binding = Some(ViewBinding {
            scope: *scope,
            collection,
            predicate: input.predicate.clone(),
        });
        self.phase = phase;
        self.rows = rows;
        Ok(())
    }

    /// Validate the complete page and its request before publishing groups and cursor.
    pub fn apply_poll(
        &mut self,
        scope: &DatabaseScope,
        collection: CollectionId,
        input: &PollRequest,
        page: &PollPage,
    ) -> Result<()> {
        input.predicate.validate()?;
        validate_cursor(&input.poll_cursor)?;
        self.validate_binding(scope, collection, &input.predicate)?;
        if !matches!(&self.phase, ViewPhase::Poll(expected) if expected == &input.poll_cursor) {
            return Err(invalid(
                "Poll request requires this view's completed snapshot and current poll cursor.",
            ));
        }
        page.validate_in(scope)?;
        let mut rows = self.rows.clone();
        let mut sequence = self.sequence;
        for group in &page.groups {
            if sequence.is_some_and(|s| group.sequence <= s) {
                return Err(invalid(
                    "Poll group regressed or repeated an already published sequence.",
                ));
            }
            for effect in &group.effects {
                if let Some(point) = &effect.point {
                    rows.insert(effect.document, point.clone());
                } else {
                    rows.remove(&effect.document);
                }
            }
            sequence = Some(group.sequence);
        }
        self.phase = ViewPhase::Poll(page.poll_cursor.clone());
        self.rows = rows;
        self.sequence = sequence;
        Ok(())
    }
}

#[derive(Debug, Clone)]
pub struct SubscriptionsClient {
    shared: SharedClient,
}

impl SubscriptionsClient {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }
    pub async fn snapshot(
        &self,
        scope: &DatabaseScope,
        collection: CollectionId,
        input: &SnapshotRequest,
    ) -> Result<SnapshotPage> {
        input.validate()?;
        let origin = self
            .shared
            .native_origin(scope, NativeRequirement::Subscription)
            .await?;
        let request = Request::json(
            "native.subscription",
            Method::Post,
            format!(
                "{origin}{}/subscriptions",
                scope.collection_path(collection)
            ),
            input,
        )?;
        let page: SnapshotPage = self
            .shared
            .send_native_typed(scope, NativeRequirement::Subscription, request)
            .await?;
        page.validate_in(scope, input.limit)
            .map_err(|e| Error::invalid_response(e.message))?;
        Ok(page)
    }
    pub async fn poll(
        &self,
        scope: &DatabaseScope,
        collection: CollectionId,
        input: &PollRequest,
    ) -> Result<PollPage> {
        input.predicate.validate()?;
        validate_cursor(&input.poll_cursor)?;
        let origin = self
            .shared
            .native_origin(scope, NativeRequirement::Subscription)
            .await?;
        let request = Request::json(
            "native.subscription",
            Method::Post,
            format!(
                "{origin}{}/subscriptions",
                scope.collection_path(collection)
            ),
            input,
        )?;
        let page: PollPage = self
            .shared
            .send_native_typed(scope, NativeRequirement::Subscription, request)
            .await?;
        page.validate_in(scope)
            .map_err(|e| Error::invalid_response(e.message))?;
        Ok(page)
    }
}
