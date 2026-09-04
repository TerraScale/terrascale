//! Atomic key-range routing contracts.

use super::CutoverPermit;

/// One shard's inclusive-lower/exclusive-upper key range.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ShardRoute {
    shard_id: u64,
    lower: Option<Vec<u8>>,
    upper: Option<Vec<u8>>,
}

impl ShardRoute {
    /// Creates a route, validating that its bounds are ordered.
    pub fn new(
        shard_id: u64,
        lower: Option<Vec<u8>>,
        upper: Option<Vec<u8>>,
    ) -> Result<Self, RouteError> {
        if lower.as_deref().is_some_and(|value| value.is_empty())
            || upper.as_deref().is_some_and(|value| value.is_empty())
        {
            return Err(RouteError::EmptyBound);
        }
        if lower.as_deref().zip(upper.as_deref()).is_some_and(|(left, right)| left >= right) {
            return Err(RouteError::BoundsReversed);
        }
        Ok(Self { shard_id, lower, upper })
    }

    /// Returns the owning shard identifier.
    #[must_use]
    pub const fn shard_id(&self) -> u64 {
        self.shard_id
    }

    /// Returns the inclusive lower bound, if any.
    #[must_use]
    pub fn lower(&self) -> Option<&[u8]> {
        self.lower.as_deref()
    }

    /// Returns the exclusive upper bound, if any.
    #[must_use]
    pub fn upper(&self) -> Option<&[u8]> {
        self.upper.as_deref()
    }

    fn contains(&self, key: &[u8]) -> bool {
        self.lower.as_deref().is_none_or(|lower| key >= lower)
            && self.upper.as_deref().is_none_or(|upper| key < upper)
    }
}

/// Why a route table update was refused.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RouteError {
    /// A bound was empty; empty is reserved for an unbounded side.
    EmptyBound,
    /// Lower bound was not less than upper bound.
    BoundsReversed,
    /// Two routes overlap.
    Overlap,
    /// The source shard was not present.
    SourceMissing,
}

/// Immutable-style route table replaced as one value at cutover.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RouteTable {
    routes: Vec<ShardRoute>,
}

impl RouteTable {
    /// Builds a validated table from routes.
    pub fn new(mut routes: Vec<ShardRoute>) -> Result<Self, RouteError> {
        routes.sort_by(|left, right| left.lower.cmp(&right.lower));
        if routes.windows(2).any(|pair| {
            pair[0]
                .upper
                .as_deref()
                .is_none_or(|upper| pair[1].lower.as_deref().is_none_or(|lower| upper > lower))
        }) {
            return Err(RouteError::Overlap);
        }
        Ok(Self { routes })
    }

    /// Creates a table with one shard owning the complete key space.
    pub fn single(shard_id: u64) -> Self {
        Self { routes: vec![ShardRoute { shard_id, lower: None, upper: None }] }
    }

    /// Returns the shard for a key, or `None` for an intentional routing gap.
    #[must_use]
    pub fn route(&self, key: &[u8]) -> Option<u64> {
        self.routes.iter().find(|route| route.contains(key)).map(ShardRoute::shard_id)
    }

    /// Returns the current route entries in key order.
    #[must_use]
    pub fn routes(&self) -> &[ShardRoute] {
        &self.routes
    }

    /// Applies a split to `source` using a fenced cutover permit.
    pub fn split(
        &mut self,
        source: u64,
        target: u64,
        upper: Vec<u8>,
        permit: CutoverPermit,
    ) -> Result<(), RouteError> {
        let _ = permit;
        if upper.is_empty() {
            return Err(RouteError::EmptyBound);
        }
        let index = self
            .routes
            .iter()
            .position(|route| route.shard_id == source && route.contains(&upper))
            .ok_or(RouteError::SourceMissing)?;
        let old = self.routes[index].clone();
        let target_route = ShardRoute::new(target, old.lower.clone(), Some(upper.clone()))?;
        let source_route = ShardRoute::new(source, Some(upper), old.upper.clone())?;
        // Build first, then swap the vector, so readers see either complete
        // old routing or complete new routing when their owner installs it.
        let mut replacement = self.routes.clone();
        replacement.splice(index..=index, [target_route, source_route]);
        *self = Self::new(replacement)?;
        Ok(())
    }
}

/// Small owner wrapper whose table replacement is the routing cutover point.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ShardRouter {
    table: RouteTable,
}

impl ShardRouter {
    /// Creates a router with one source shard.
    #[must_use]
    pub fn single(source: u64) -> Self {
        Self { table: RouteTable::single(source) }
    }

    /// Creates a router around a validated table.
    #[must_use]
    pub const fn from_table(table: RouteTable) -> Self {
        Self { table }
    }

    /// Looks up a key in the current routing generation.
    #[must_use]
    pub fn route(&self, key: &[u8]) -> Option<u64> {
        self.table.route(key)
    }

    /// Returns the current immutable route table.
    #[must_use]
    pub const fn table(&self) -> &RouteTable {
        &self.table
    }

    /// Atomically installs a split route after writer fencing.
    pub fn cut_over(
        &mut self,
        source: u64,
        target: u64,
        upper: Vec<u8>,
        permit: CutoverPermit,
    ) -> Result<(), RouteError> {
        self.table.split(source, target, upper, permit)
    }
}
