//! Spatial foundation (GAP-Q14, TerraBaseV2#68): the units, coordinate,
//! shape-encoding, and predicate vocabulary every V2 spatial feature is
//! built on.
//!
//! V1 exposes spatial query as four relations over circle and WKT shapes
//! with kilometer/mile units and a distance-error percentage
//! (`SpatialCriteriaFactory`, `SpatialRelation`, `SpatialUnits` — see the
//! citation table in `docs/query/spatial-foundation.md`). This module
//! fixes the V2 foundation of that surface as typed values with one
//! validated construction path each:
//!
//! - [`Longitude`]/[`Latitude`]/[`Wgs84Point`] — the WGS84 degree
//!   coordinate contract, the same axis order and domain V1's geography
//!   context uses (x = longitude in `[-180, 180]`, y = latitude in
//!   `[-90, 90]`).
//! - [`SpatialUnits`] with the V1 distance arithmetic: the haversine is
//!   computed in international nautical miles and scaled by exactly the
//!   V1 factors, so distance values match V1's Corax evaluator.
//! - [`SpatialShape`] — point, circle, linestring, polygon, and envelope
//!   shapes parsed from the bounded WKT subset V1 accepts (including its
//!   `Circle(x y d=r)`, bare-rectangle, and `BOX(2D)` forms, with Z/M
//!   ordinates discarded the way V1's `WktSanitizer` discards them).
//! - [`SpatialFieldEncoding`] — how one document exposes a spatial
//!   value: two numeric fields (latitude, longitude) or one WKT field,
//!   rendered to the V1 dynamic-field token.
//! - [`SpatialPredicate`] — the typed relation/shape/error-percentage
//!   triple with bounded evaluation against point documents.
//! - [`SpatialPrecision`] — the coordinate discretization rule spatial
//!   key derivation quantizes with.
//!
//! Decision-first scope and non-claims. The geometry base is V2-internal
//! (the dependency audit recorded in the decision record proposed that,
//! pending ratification); no geometry crate is on the engine's
//! dependency closure. Evaluation is defined for point documents only —
//! V1's Corax evaluator at the pinned commit also indexes and matches
//! points only, while its Lucene side indexes full WKT documents;
//! shape-document evaluation, spatial index structures, geohash or
//! quad-prefix trees, spatial distance ordering integration, and spatial
//! boosting are not implemented here and remain open parity work. The
//! `distanceErrorPct` parameter is carried and validated but no
//! approximation is applied: exact evaluation needs no error budget.

use crate::operations::RequestValidationError;
use crate::query::spec::validate_field_name;

/// The maximum accepted WKT input, in bytes. Sized so the largest accepted
/// polygon (all rings at [`MAX_SHAPE_POINTS`]) still fits; V1 imposes no
/// input bound, so every larger input refuses here instead of parsing.
pub const MAX_WKT_BYTES: usize = 65_536;

/// The maximum number of positions in one linestring or polygon ring.
pub const MAX_SHAPE_POINTS: usize = 256;

/// The maximum number of rings in one polygon: one exterior ring plus at
/// most seven holes.
pub const MAX_POLYGON_RINGS: usize = 8;

/// The inclusive upper bound of a spatial predicate's distance-error
/// percentage (V1 rejects values outside `[0, 0.5]` when it derives the
/// error radius).
pub const MAX_DISTANCE_ERROR_PCT: f64 = 0.5;

/// The default distance-error percentage V1 applies to spatial criteria.
pub const DEFAULT_DISTANCE_ERROR_PCT: f64 = 0.025;

/// The Earth mean radius in kilometers V1 uses to translate circle radii
/// into angular degrees.
pub const EARTH_MEAN_RADIUS_KM: f64 = 6371.008_771_4;

/// The miles-to-kilometers factor V1 uses before that translation.
pub const MILES_TO_KM: f64 = 1.609_34;

/// International nautical miles per degree of great-circle arc V1's
/// distance formula uses.
const NAUTICAL_MILES_PER_DEGREE: f64 = 60.0;

/// Kilometers per international nautical mile (V1's factor).
const KILOMETERS_PER_NAUTICAL_MILE: f64 = 1.852;

/// Miles per international nautical mile (V1's factor).
const MILES_PER_NAUTICAL_MILE: f64 = 1.1515;

/// The inclusive upper bound of spatial coordinate quantization, in
/// decimal places of a degree.
pub const MAX_PRECISION_DECIMALS: u8 = 9;

/// The default spatial coordinate quantization: six decimals of a degree
/// (about 0.11 m of longitude at the equator), finer than the coarsest
/// query-visible discretization V1 defaults to (geohash level 9, about
/// 4.78 m at the equator), so quantization never coarsens a comparison
/// V1 would make.
pub const DEFAULT_PRECISION_DECIMALS: u8 = 6;

/// Why a spatial shape string or coordinate refused construction.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SpatialShapeError {
    /// The input was empty or only whitespace.
    Empty,
    /// The input exceeded [`MAX_WKT_BYTES`].
    TooLarge,
    /// The input names a shape kind this foundation does not accept.
    Unsupported,
    /// The input is not well-formed for the shape kind it names.
    Malformed,
    /// A coordinate was not a finite number.
    CoordinateNotFinite,
    /// A coordinate was outside the WGS84 domain for its axis.
    CoordinateOutOfRange,
    /// A linestring or ring exceeded [`MAX_SHAPE_POINTS`] positions.
    TooManyPoints,
    /// A polygon exceeded [`MAX_POLYGON_RINGS`] rings.
    TooManyRings,
}

/// A longitude in WGS84 degrees on the inclusive domain `[-180, 180]`.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Longitude(f64);

impl Longitude {
    /// Validates one longitude: finite and within `[-180, 180]`.
    ///
    /// # Errors
    ///
    /// Returns [`SpatialShapeError::CoordinateNotFinite`] for a non-finite
    /// value and [`SpatialShapeError::CoordinateOutOfRange`] outside the
    /// domain. Values at exactly the poles of the domain are valid, so
    /// neither edge of the antimeridian is rejected.
    pub fn try_new(degrees: f64) -> Result<Self, SpatialShapeError> {
        validate_axis(degrees, -180.0, 180.0).map(Self)
    }

    /// Returns the longitude in degrees.
    #[must_use]
    pub fn degrees(self) -> f64 {
        self.0
    }
}

/// A latitude in WGS84 degrees on the inclusive domain `[-90, 90]`.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Latitude(f64);

impl Latitude {
    /// Validates one latitude: finite and within `[-90, 90]`.
    ///
    /// # Errors
    ///
    /// Returns [`SpatialShapeError::CoordinateNotFinite`] for a non-finite
    /// value and [`SpatialShapeError::CoordinateOutOfRange`] outside the
    /// domain.
    pub fn try_new(degrees: f64) -> Result<Self, SpatialShapeError> {
        validate_axis(degrees, -90.0, 90.0).map(Self)
    }

    /// Returns the latitude in degrees.
    #[must_use]
    pub fn degrees(self) -> f64 {
        self.0
    }
}

/// Validates one coordinate axis value against its inclusive domain.
fn validate_axis(degrees: f64, min: f64, max: f64) -> Result<f64, SpatialShapeError> {
    if degrees.is_nan() || degrees.is_infinite() {
        return Err(SpatialShapeError::CoordinateNotFinite);
    }
    if !(min..=max).contains(&degrees) {
        return Err(SpatialShapeError::CoordinateOutOfRange);
    }
    Ok(degrees)
}

/// One point on the WGS84 degree grid: x = longitude, y = latitude, the
/// same axis order V1's geography context and WKT reader use (a `geo:`
/// URI is latitude-first and V1 swaps it on read; WKT is not).
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Wgs84Point {
    longitude: Longitude,
    latitude: Latitude,
}

impl Wgs84Point {
    /// Creates one point from two already-validated axis values: after
    /// [`Longitude::try_new`] and [`Latitude::try_new`] there is no way
    /// left for a point to be invalid, so construction is infallible.
    #[must_use]
    pub fn new(longitude: Longitude, latitude: Latitude) -> Self {
        Self {
            longitude,
            latitude,
        }
    }

    /// Returns the point's longitude.
    #[must_use]
    pub fn longitude(&self) -> Longitude {
        self.longitude
    }

    /// Returns the point's latitude.
    #[must_use]
    pub fn latitude(&self) -> Latitude {
        self.latitude
    }

    /// The great-circle separation from this point to another, in degrees.
    ///
    /// This is V1's haversine before its nautical-mile scaling: the
    /// spherical law of cosines evaluated on the degree inputs, clamped
    /// only on the high side exactly as V1 clamps it. Identical points
    /// return exactly zero.
    #[must_use]
    pub fn angular_distance_degrees(&self, other: &Self) -> f64 {
        // The exact-equality guard is V1's own: identical inputs short
        // circuit to zero before the law of cosines runs.
        #[allow(
            clippy::float_cmp,
            reason = "V1's distance formula guards identical coordinates with exact equality"
        )]
        let identical = self.latitude.degrees() == other.latitude.degrees()
            && self.longitude.degrees() == other.longitude.degrees();
        if identical {
            return 0.0;
        }
        let lat_a = self.latitude.degrees().to_radians();
        let lat_b = other.latitude.degrees().to_radians();
        let delta_longitude = (self.longitude.degrees() - other.longitude.degrees()).to_radians();
        let cosine = lat_a.sin() * lat_b.sin() + lat_a.cos() * lat_b.cos() * delta_longitude.cos();
        let cosine = if cosine > 1.0 { 1.0 } else { cosine };
        cosine.acos().to_degrees()
    }

    /// The distance from this point to another in the requested units,
    /// using V1's arithmetic: nautical miles per degree, then the unit's
    /// factor. Kilometer and mile distances therefore carry V1's
    /// conversion constants (111.12 km and 69.09 miles per degree), not
    /// the Earth-mean-radius conversion V1 uses for circle radii — the
    /// two V1 conversions disagree by about 0.07 percent, and this
    /// foundation reproduces each in its V1 role rather than unifying
    /// them.
    #[must_use]
    pub fn distance(&self, other: &Self, units: SpatialUnits) -> f64 {
        self.angular_distance_degrees(other)
            * NAUTICAL_MILES_PER_DEGREE
            * units.factor_from_nautical_miles()
    }

    /// Rounds one distance the way V1 rounds spatial ordering distances:
    /// `distance - distance % round` for a positive round factor, and the
    /// distance unchanged otherwise.
    #[must_use]
    pub fn apply_rounding(distance: f64, round: f64) -> f64 {
        if round <= 0.0 {
            return distance;
        }
        distance - distance % round
    }

    /// Quantizes this point into the discrete cell of one precision
    /// level.
    ///
    /// The cell floors `(degree + domain offset) * 10^decimals` per axis,
    /// so the mapping is monotone (a point strictly east and north of
    /// another never lands in a smaller cell) and idempotent (two points
    /// in the same cell quantize identically, and re-quantizing a
    /// coordinate that is already on cell boundaries is stable).
    #[must_use]
    pub fn quantize(&self, precision: SpatialPrecision) -> QuantizedCell {
        QuantizedCell::of(self, precision)
    }
}

/// The units a spatial radius or distance is expressed in. V1's set is
/// exactly these two.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SpatialUnits {
    /// Kilometers.
    Kilometers,
    /// International miles.
    Miles,
}

impl SpatialUnits {
    /// The factor V1 multiplies a haversine nautical-mile result by to
    /// obtain this unit.
    #[must_use]
    pub fn factor_from_nautical_miles(self) -> f64 {
        match self {
            Self::Kilometers => KILOMETERS_PER_NAUTICAL_MILE,
            Self::Miles => MILES_PER_NAUTICAL_MILE,
        }
    }
}

/// One closed polygon ring: at least three positions, with closure
/// implicit (an explicitly repeated last position is normalized away).
type Ring = Vec<Wgs84Point>;

/// A polygon: one exterior ring and zero or more hole rings, matched with
/// planar even-odd semantics on the longitude/latitude plane. Ring edges
/// are straight in degree space — they do not follow geodesics and do not
/// wrap the antimeridian.
#[derive(Clone, Debug, PartialEq)]
pub struct SpatialPolygon {
    exterior: Ring,
    holes: Vec<Ring>,
}

impl SpatialPolygon {
    /// Creates one polygon from already-validated points.
    ///
    /// # Errors
    ///
    /// Returns [`SpatialShapeError::Malformed`] when the exterior or any
    /// hole has fewer than three positions, and the bound errors when a
    /// ring or the hole count exceeds this foundation's limits.
    pub fn try_new(exterior: Ring, holes: Vec<Ring>) -> Result<Self, SpatialShapeError> {
        let mut rings = Vec::with_capacity(1 + holes.len());
        rings.push(exterior);
        rings.extend(holes);
        Self::try_from_rings(rings)
    }

    /// Creates one polygon from rings, validating each ring and the ring
    /// count.
    fn try_from_rings(rings: Vec<Ring>) -> Result<Self, SpatialShapeError> {
        if rings.len() > MAX_POLYGON_RINGS {
            return Err(SpatialShapeError::TooManyRings);
        }
        let mut normalized = Vec::with_capacity(rings.len());
        for ring in rings {
            normalized.push(normalize_ring(ring)?);
        }
        let mut rings = normalized.into_iter();
        let exterior = rings.next().ok_or(SpatialShapeError::Malformed)?;
        Ok(Self { exterior, holes: rings.collect() })
    }

    /// Returns the exterior ring.
    #[must_use]
    pub fn exterior(&self) -> &[Wgs84Point] {
        &self.exterior
    }

    /// Returns the hole rings.
    #[must_use]
    pub fn holes(&self) -> &[Ring] {
        &self.holes
    }

    /// Whether a point is inside the polygon or on any of its ring
    /// boundaries. A point inside a hole is outside the polygon unless it
    /// is on the hole's boundary.
    #[must_use]
    pub fn contains_point(&self, point: &Wgs84Point) -> bool {
        if !ring_interior(&self.exterior, point) && !ring_boundary(&self.exterior, point) {
            return false;
        }
        for hole in &self.holes {
            if ring_boundary(hole, point) {
                return true;
            }
            if ring_interior(hole, point) {
                return false;
            }
        }
        true
    }
}

/// Drops an explicitly repeated last position and validates one ring.
fn normalize_ring(ring: Ring) -> Result<Ring, SpatialShapeError> {
    if ring.len() > MAX_SHAPE_POINTS {
        return Err(SpatialShapeError::TooManyPoints);
    }
    let mut ring = ring;
    if ring.len() > 3 && ring.first() == ring.last() {
        ring.pop();
    }
    if ring.len() < 3 {
        return Err(SpatialShapeError::Malformed);
    }
    Ok(ring)
}

/// Whether `point` lies strictly inside one ring, by even-odd ray casting
/// on the degree plane.
fn ring_interior(ring: &[Wgs84Point], point: &Wgs84Point) -> bool {
    let mut inside = false;
    let mut previous = ring.len() - 1;
    for current in 0..ring.len() {
        let a = ring[current];
        let b = ring[previous];
        if (a.latitude.degrees() > point.latitude.degrees())
            != (b.latitude.degrees() > point.latitude.degrees())
        {
            let crossing = (b.longitude.degrees() - a.longitude.degrees())
                * (point.latitude.degrees() - a.latitude.degrees())
                / (b.latitude.degrees() - a.latitude.degrees())
                + a.longitude.degrees();
            if point.longitude.degrees() < crossing {
                inside = !inside;
            }
        }
        previous = current;
    }
    inside
}

/// Whether `point` lies exactly on one ring's boundary, by zero cross
/// product and segment bounding box.
fn ring_boundary(ring: &[Wgs84Point], point: &Wgs84Point) -> bool {
    let mut previous = ring.len() - 1;
    for current in 0..ring.len() {
        if point_on_segment(point, &ring[previous], &ring[current]) {
            return true;
        }
        previous = current;
    }
    false
}

/// Whether `point` lies on the closed segment `a`-`b` in degree space.
fn point_on_segment(point: &Wgs84Point, a: &Wgs84Point, b: &Wgs84Point) -> bool {
    let cross = (b.longitude.degrees() - a.longitude.degrees())
        * (point.latitude.degrees() - a.latitude.degrees())
        - (b.latitude.degrees() - a.latitude.degrees())
            * (point.longitude.degrees() - a.longitude.degrees());
    // Exact-zero cross product is the collinearity test: a tolerance
    // would either drop genuine boundary hits or admit near misses, and
    // V1's relate machinery tests boundary membership the same exact way.
    #[allow(
        clippy::float_cmp,
        reason = "zero cross product is the exact collinearity test for boundary membership"
    )]
    {
        if cross != 0.0 {
            return false;
        }
    }
    let x = point.longitude.degrees();
    let y = point.latitude.degrees();
    let min_x = a.longitude.degrees().min(b.longitude.degrees());
    let max_x = a.longitude.degrees().max(b.longitude.degrees());
    let min_y = a.latitude.degrees().min(b.latitude.degrees());
    let max_y = a.latitude.degrees().max(b.latitude.degrees());
    (min_x..=max_x).contains(&x) && (min_y..=max_y).contains(&y)
}

/// One query or document shape in the subset this foundation accepts.
///
/// The variants mirror the shape kinds V1's query surface accepts: WKT
/// points, linestrings, and polygons, plus V1's circle form (a center, a
/// radius, and the units the radius is read in) and its rectangle forms
/// (the bare `x1 y1 x2 y2` string and `BOX(2D)(...)`, both of which V1
/// lowers to a rectangle).
#[derive(Clone, Debug, PartialEq)]
pub enum SpatialShape {
    /// A single point.
    Point(Wgs84Point),
    /// A circle: center, radius in `units`, radius strictly positive.
    Circle {
        /// The circle center.
        center: Wgs84Point,
        /// The radius in [`units`](Self::Circle::units).
        radius: f64,
        /// The units the radius is expressed in.
        units: SpatialUnits,
    },
    /// A linestring of two to [`MAX_SHAPE_POINTS`] positions.
    LineString {
        /// The positions, in order.
        points: Vec<Wgs84Point>,
    },
    /// A polygon with holes.
    Polygon(SpatialPolygon),
    /// A rectangle in degree space. `west` greater than `east` expresses a
    /// rectangle wrapping the antimeridian; `south` is never greater than
    /// `north`.
    Envelope {
        /// The western longitude bound.
        west: Longitude,
        /// The southern latitude bound.
        south: Latitude,
        /// The eastern longitude bound.
        east: Longitude,
        /// The northern latitude bound.
        north: Latitude,
    },
}

impl SpatialShape {
    /// Translates one circle radius into great-circle degrees the way V1
    /// translates it before matching: miles convert to kilometers first,
    /// then the radius divides by the Earth mean radius and converts to
    /// degrees. Circle matching therefore uses the Earth-mean-radius
    /// conversion (111.195 km per degree), which is the V1 behavior for
    /// circle predicates and deliberately not the nautical-mile chain
    /// [`Wgs84Point::distance`] reproduces for distance values.
    #[must_use]
    pub fn circle_radius_degrees(radius: f64, units: SpatialUnits) -> f64 {
        let kilometers = match units {
            SpatialUnits::Kilometers => radius,
            SpatialUnits::Miles => radius * MILES_TO_KM,
        };
        kilometers / EARTH_MEAN_RADIUS_KM * (180.0 / std::f64::consts::PI)
    }

    /// Parses one bounded shape string in V1's accepted forms.
    ///
    /// Accepted: `POINT`, `LINESTRING`, `POLYGON`, V1's `Circle(x y d=r)`
    /// form, and V1's rectangle forms — the bare `x1 y1 x2 y2` string and
    /// `BOX(2D)(x1 y1, x2 y2)`. Tags are case-insensitive; whitespace is
    /// free; coordinates beyond the first two ordinates (Z/M values and
    /// dimension flags) are discarded, as V1's sanitizer discards them;
    /// polygon rings need not repeat their first position. Numbers use
    /// V1's decimal grammar (no exponent notation).
    ///
    /// # Errors
    ///
    /// Returns the typed refusal for empty, oversized, unsupported,
    /// malformed, out-of-domain, or out-of-bounds inputs. A circle radius
    /// that is not finite and strictly positive refuses as malformed.
    pub fn parse(input: &str, units: SpatialUnits) -> Result<Self, SpatialShapeError> {
        let trimmed = input.trim();
        if trimmed.is_empty() {
            return Err(SpatialShapeError::Empty);
        }
        if trimmed.len() > MAX_WKT_BYTES {
            return Err(SpatialShapeError::TooLarge);
        }
        let mut cursor = WktCursor::new(trimmed.as_bytes());
        cursor.skip_ws();
        if cursor.eat_tag("POINT") {
            return Self::parse_point(&mut cursor);
        }
        if cursor.eat_tag("CIRCLE") {
            return Self::parse_circle(&mut cursor, units);
        }
        if cursor.eat_tag("LINESTRING") {
            return Self::parse_linestring(&mut cursor);
        }
        if cursor.eat_tag("POLYGON") {
            return Self::parse_polygon(&mut cursor);
        }
        if cursor.eat_tag("BOX2D") || cursor.eat_tag("BOX") {
            return Self::parse_box(&mut cursor);
        }
        if trimmed.as_bytes()[0].is_ascii_digit() || trimmed.starts_with(['+', '-', '.']) {
            return Self::parse_bare_rectangle(&mut cursor);
        }
        Err(SpatialShapeError::Unsupported)
    }

    /// Parses `POINT (x y ...)`.
    fn parse_point(cursor: &mut WktCursor<'_>) -> Result<Self, SpatialShapeError> {
        cursor.skip_dimension_flag();
        cursor.eat_open()?;
        cursor.skip_ws();
        let point = cursor.coordinate()?;
        cursor.skip_ws();
        cursor.expect_close()?;
        Ok(Self::Point(point))
    }

    /// Parses V1's `Circle(x y d=r)` form.
    fn parse_circle(
        cursor: &mut WktCursor<'_>,
        units: SpatialUnits,
    ) -> Result<Self, SpatialShapeError> {
        cursor.skip_ws();
        cursor.eat_open()?;
        cursor.skip_ws();
        let center = cursor.coordinate()?;
        cursor.skip_ws();
        if !cursor.eat_keyword("d=") {
            return Err(SpatialShapeError::Malformed);
        }
        let radius = cursor.number()?;
        cursor.skip_ws();
        cursor.expect_close()?;
        if !radius.is_finite() || radius <= 0.0 {
            return Err(SpatialShapeError::Malformed);
        }
        Ok(Self::Circle { center, radius, units })
    }

    /// Parses `LINESTRING (x y, ...)`.
    fn parse_linestring(cursor: &mut WktCursor<'_>) -> Result<Self, SpatialShapeError> {
        cursor.skip_dimension_flag();
        cursor.eat_open()?;
        cursor.skip_ws();
        let mut points = Vec::new();
        loop {
            points.push(cursor.coordinate()?);
            cursor.skip_ws();
            if !cursor.eat(b',') {
                break;
            }
            cursor.skip_ws();
        }
        if points.len() < 2 {
            return Err(SpatialShapeError::Malformed);
        }
        if points.len() > MAX_SHAPE_POINTS {
            return Err(SpatialShapeError::TooManyPoints);
        }
        cursor.expect_close()?;
        Ok(Self::LineString { points })
    }

    /// Parses `POLYGON ((...), (...))`.
    fn parse_polygon(cursor: &mut WktCursor<'_>) -> Result<Self, SpatialShapeError> {
        cursor.skip_dimension_flag();
        cursor.eat_open()?;
        cursor.skip_ws();
        let mut rings = Vec::new();
        loop {
            cursor.eat_open()?;
            cursor.skip_ws();
            let mut ring = Vec::new();
            loop {
                ring.push(cursor.coordinate()?);
                cursor.skip_ws();
                if !cursor.eat(b',') {
                    break;
                }
                cursor.skip_ws();
            }
            // An inner ring closes without ending the input; only the
            // polygon's outer parenthesis ends it.
            if !cursor.eat(b')') {
                return Err(SpatialShapeError::Malformed);
            }
            rings.push(ring);
            cursor.skip_ws();
            if !cursor.eat(b',') {
                break;
            }
            cursor.skip_ws();
        }
        cursor.expect_close()?;
        SpatialPolygon::try_from_rings(rings).map(Self::Polygon)
    }

    /// Parses `BOX(2D)(x1 y1, x2 y2)`.
    fn parse_box(cursor: &mut WktCursor<'_>) -> Result<Self, SpatialShapeError> {
        cursor.eat_open()?;
        cursor.skip_ws();
        let (x1, y1) = cursor.coordinate()?.into_axes();
        cursor.skip_ws();
        if !cursor.eat(b',') {
            return Err(SpatialShapeError::Malformed);
        }
        cursor.skip_ws();
        let (x2, y2) = cursor.coordinate()?.into_axes();
        cursor.skip_ws();
        cursor.expect_close()?;
        Self::envelope_from_corners(x1, y1, x2, y2)
    }

    /// Parses the bare `x1 y1 x2 y2` rectangle form.
    ///
    /// This form is exactly four numbers and no parentheses, so it reads
    /// raw numbers rather than positions: a position reader would
    /// legitimately discard the third and fourth numbers as extra
    /// ordinates.
    fn parse_bare_rectangle(cursor: &mut WktCursor<'_>) -> Result<Self, SpatialShapeError> {
        let x1 = cursor.number()?;
        cursor.skip_ws();
        let y1 = cursor.number()?;
        cursor.skip_ws();
        let x2 = cursor.number()?;
        cursor.skip_ws();
        let y2 = cursor.number()?;
        cursor.skip_ws();
        if !cursor.at_end() {
            return Err(SpatialShapeError::Malformed);
        }
        Self::envelope_from_corners(x1, y1, x2, y2)
    }

    /// Builds the normalized envelope of two rectangle corners: the axis
    /// minima become the south and west bounds and the maxima the north
    /// and east bounds, so corner order never matters.
    fn envelope_from_corners(
        x1: f64,
        y1: f64,
        x2: f64,
        y2: f64,
    ) -> Result<Self, SpatialShapeError> {
        Ok(Self::Envelope {
            west: Longitude::try_new(x1.min(x2))?,
            south: Latitude::try_new(y1.min(y2))?,
            east: Longitude::try_new(x1.max(x2))?,
            north: Latitude::try_new(y1.max(y2))?,
        })
    }

    /// Whether this shape is a point.
    #[must_use]
    pub fn is_point(&self) -> bool {
        matches!(self, Self::Point(_))
    }

    /// Returns the circle parameters when this shape is a circle.
    #[must_use]
    pub fn as_circle(&self) -> Option<(&Wgs84Point, f64, SpatialUnits)> {
        match self {
            Self::Circle { center, radius, units } => Some((center, *radius, *units)),
            _ => None,
        }
    }

    /// Returns the polygon when this shape is a polygon.
    #[must_use]
    pub fn as_polygon(&self) -> Option<&SpatialPolygon> {
        match self {
            Self::Polygon(polygon) => Some(polygon),
            _ => None,
        }
    }

    /// Whether a point belongs to this shape's extent, boundary included.
    ///
    /// For a point shape this is exact equality; for a circle it compares
    /// angular separation against the translated radius; for a linestring
    /// the point must lie on one of its segments; for polygons and
    /// envelopes the planar containment rules of those shapes apply.
    #[must_use]
    pub fn contains_point(&self, point: &Wgs84Point) -> bool {
        match self {
            Self::Point(candidate) => candidate == point,
            Self::Circle { center, radius, units } => {
                center.angular_distance_degrees(point)
                    <= Self::circle_radius_degrees(*radius, *units)
            }
            Self::LineString { points } => {
                let mut previous = points.len() - 1;
                for current in 0..points.len() {
                    if point_on_segment(point, &points[previous], &points[current]) {
                        return true;
                    }
                    previous = current;
                }
                false
            }
            Self::Polygon(polygon) => polygon.contains_point(point),
            Self::Envelope { west, south, east, north } => {
                let x = point.longitude.degrees();
                let y = point.latitude.degrees();
                let longitude_inside = if west.degrees() <= east.degrees() {
                    x >= west.degrees() && x <= east.degrees()
                } else {
                    // west > east is a rectangle wrapping the antimeridian:
                    // the extent is the union of both longitude spans.
                    x >= west.degrees() || x <= east.degrees()
                };
                longitude_inside && y >= south.degrees() && y <= north.degrees()
            }
        }
    }
}

/// A byte cursor over one WKT input. Numbers use V1's decimal grammar.
struct WktCursor<'a> {
    bytes: &'a [u8],
    position: usize,
}

impl WktCursor<'_> {
    /// Creates one cursor at the input start.
    fn new(bytes: &[u8]) -> WktCursor<'_> {
        WktCursor { bytes, position: 0 }
    }

    /// Skips ASCII whitespace.
    fn skip_ws(&mut self) {
        while self.position < self.bytes.len() && self.bytes[self.position].is_ascii_whitespace() {
            self.position += 1;
        }
    }

    /// Whether the cursor consumed the whole input.
    fn at_end(&mut self) -> bool {
        self.skip_ws();
        self.position == self.bytes.len()
    }

    /// Consumes one literal byte, ASCII case-insensitively for letters.
    fn eat(&mut self, expected: u8) -> bool {
        if self.position >= self.bytes.len() {
            return false;
        }
        let actual = self.bytes[self.position];
        let matched = if expected.is_ascii_alphabetic() {
            actual.eq_ignore_ascii_case(&expected)
        } else {
            actual == expected
        };
        if matched {
            self.position += 1;
        }
        matched
    }

    /// Consumes one case-insensitive keyword when it prefixes the rest of
    /// the input.
    fn eat_keyword(&mut self, keyword: &str) -> bool {
        let rest = &self.bytes[self.position..];
        if rest.len() < keyword.len() {
            return false;
        }
        if rest[..keyword.len()]
            .iter()
            .zip(keyword.as_bytes())
            .all(|(actual, expected)| actual.eq_ignore_ascii_case(expected))
        {
            self.position += keyword.len();
            return true;
        }
        false
    }

    /// Consumes one shape tag when it prefixes the input.
    fn eat_tag(&mut self, tag: &str) -> bool {
        let saved = self.position;
        if !self.eat_keyword(tag) {
            return false;
        }
        // A tag must end before a separator so BOX2D does not match BOX.
        if self.position < self.bytes.len() {
            let next = self.bytes[self.position];
            if next.is_ascii_alphanumeric() || next == b'_' {
                self.position = saved;
                return false;
            }
        }
        true
    }

    /// Consumes the opening parenthesis of a shape body.
    fn eat_open(&mut self) -> Result<(), SpatialShapeError> {
        self.skip_ws();
        if self.eat(b'(') { Ok(()) } else { Err(SpatialShapeError::Malformed) }
    }

    /// Consumes the closing parenthesis and requires the input to end.
    fn expect_close(&mut self) -> Result<(), SpatialShapeError> {
        if self.eat(b')') && self.at_end() { Ok(()) } else { Err(SpatialShapeError::Malformed) }
    }

    /// Consumes one V1 decimal number: optional sign, digits, optional
    /// fractional part, at least one digit overall, no exponent.
    fn number(&mut self) -> Result<f64, SpatialShapeError> {
        let start = self.position;
        let mut end = self.position;
        if end < self.bytes.len() && (self.bytes[end] == b'+' || self.bytes[end] == b'-') {
            end += 1;
        }
        let mut digits = 0;
        while end < self.bytes.len() && self.bytes[end].is_ascii_digit() {
            end += 1;
            digits += 1;
        }
        if end < self.bytes.len() && self.bytes[end] == b'.' {
            end += 1;
            while end < self.bytes.len() && self.bytes[end].is_ascii_digit() {
                end += 1;
                digits += 1;
            }
        }
        if digits == 0 {
            return Err(SpatialShapeError::Malformed);
        }
        self.position = end;
        let text = std::str::from_utf8(&self.bytes[start..end])
            .map_err(|_| SpatialShapeError::Malformed)?;
        text.parse::<f64>().map_err(|_| SpatialShapeError::Malformed)
    }

    /// Consumes one coordinate pair, keeping the first two ordinates and
    /// discarding further ones the way V1's reducer does.
    fn coordinate(&mut self) -> Result<Wgs84Point, SpatialShapeError> {
        let x = self.number()?;
        self.skip_ws();
        let y = self.number()?;
        self.skip_ws();
        while self.position < self.bytes.len() {
            let next = self.bytes[self.position];
            if next.is_ascii_digit() || next == b'+' || next == b'-' || next == b'.' {
                let _ = self.number()?;
                self.skip_ws();
            } else {
                break;
            }
        }
        Ok(Wgs84Point::new(Longitude::try_new(x)?, Latitude::try_new(y)?))
    }

    /// Consumes one optional Z/M/ZM dimension flag before a parenthesized
    /// body, mirroring V1's dimension-flag reduction.
    fn skip_dimension_flag(&mut self) {
        let saved = self.position;
        self.skip_ws();
        for flag in ["zm", "z", "m"] {
            if self.eat_keyword(flag) {
                self.skip_ws();
                if self.position < self.bytes.len() && self.bytes[self.position] == b'(' {
                    return;
                }
                // Not a dimension flag: restore and stop looking.
                self.position = saved;
                return;
            }
        }
        self.position = saved;
    }
}

impl Wgs84Point {
    /// Returns the point's axes as raw degrees.
    fn into_axes(self) -> (f64, f64) {
        (self.longitude.degrees(), self.latitude.degrees())
    }
}

/// The four spatial relations V1's query surface supports.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SpatialRelation {
    /// The document geometry lies inside the query shape.
    Within,
    /// The document geometry contains the query shape.
    Contains,
    /// The document geometry and the query shape share no point.
    Disjoint,
    /// The document geometry and the query shape share at least one point.
    Intersects,
}

/// How one document exposes a spatial value, in the two encodings V1's
/// dynamic spatial fields define.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum SpatialFieldEncoding {
    /// Two numeric document fields: latitude and longitude in degrees.
    Point {
        /// The field holding the latitude.
        latitude_field: String,
        /// The field holding the longitude.
        longitude_field: String,
    },
    /// One document field holding a shape string this foundation parses.
    Wkt {
        /// The field holding the shape string.
        field: String,
    },
}

impl SpatialFieldEncoding {
    /// Creates one two-field point encoding from validated field names.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidQuery`] when either field
    /// name is not a valid query field name.
    pub fn try_new_point(
        latitude_field: &str,
        longitude_field: &str,
    ) -> Result<Self, RequestValidationError> {
        validate_field_name(latitude_field)?;
        validate_field_name(longitude_field)?;
        Ok(Self::Point {
            latitude_field: latitude_field.to_string(),
            longitude_field: longitude_field.to_string(),
        })
    }

    /// Creates one WKT-field encoding from a validated field name.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidQuery`] when the field
    /// name is not a valid query field name.
    pub fn try_new_wkt(field: &str) -> Result<Self, RequestValidationError> {
        validate_field_name(field)?;
        Ok(Self::Wkt { field: field.to_string() })
    }

    /// Renders this encoding to the exact dynamic-field token string V1
    /// derives from the same encoding, so stored queries and index
    /// definitions cross-read.
    #[must_use]
    pub fn to_v1_field_token(&self) -> String {
        match self {
            Self::Point { latitude_field, longitude_field } => {
                format!("spatial.point({latitude_field}, {longitude_field})")
            }
            Self::Wkt { field } => format!("spatial.wkt({field})"),
        }
    }

    /// Whether this encoding is the WKT-field form.
    #[must_use]
    pub fn is_wkt(&self) -> bool {
        matches!(self, Self::Wkt { .. })
    }
}

/// One typed spatial predicate: the document encoding it applies to, the
/// relation it asserts, the query shape, and the distance-error
/// percentage.
///
/// Evaluation against a point document is bounded: the worst accepted
/// shape costs one pass over at most [`MAX_POLYGON_RINGS`] rings of
/// [`MAX_SHAPE_POINTS`] positions, and every accepted shape was bounded
/// at parse. The distance-error percentage is carried and validated — a
/// value in `[0, 0.5]` as V1 requires — but exact evaluation applies no
/// approximation, so the parameter does not change any answer.
#[derive(Clone, Debug, PartialEq)]
pub struct SpatialPredicate {
    encoding: SpatialFieldEncoding,
    relation: SpatialRelation,
    shape: SpatialShape,
    distance_error_pct: f64,
}

impl SpatialPredicate {
    /// Creates one predicate with an explicit distance-error percentage.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidQuery`] when the
    /// percentage is not finite or lies outside
    /// `[0, MAX_DISTANCE_ERROR_PCT]`.
    pub fn try_new(
        encoding: SpatialFieldEncoding,
        relation: SpatialRelation,
        shape: SpatialShape,
        distance_error_pct: f64,
    ) -> Result<Self, RequestValidationError> {
        if !distance_error_pct.is_finite()
            || distance_error_pct < 0.0
            || distance_error_pct > MAX_DISTANCE_ERROR_PCT
        {
            return Err(RequestValidationError::InvalidQuery);
        }
        Ok(Self { encoding, relation, shape, distance_error_pct })
    }

    /// Creates one predicate carrying V1's default distance-error
    /// percentage.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidQuery`] only through the
    /// encoding's own construction.
    pub fn with_default_error_pct(
        encoding: SpatialFieldEncoding,
        relation: SpatialRelation,
        shape: SpatialShape,
    ) -> Result<Self, RequestValidationError> {
        Self::try_new(encoding, relation, shape, DEFAULT_DISTANCE_ERROR_PCT)
    }

    /// Evaluates this predicate against one point document.
    ///
    /// For point documents — the only document geometry this foundation
    /// defines — `Within` and `Intersects` both hold exactly when the
    /// point belongs to the shape, boundary included: a point has no
    /// extent, so containment and intersection coincide. `Disjoint` is
    /// the negation. `Contains` holds only when the query shape is the
    /// very same point: a point document cannot contain an extent. V1's
    /// Corax evaluator instead scores a contained point as satisfying a
    /// `Contains` query; that collapse is recorded as a divergence for
    /// ratification, not reproduced.
    #[must_use]
    pub fn matches_point(&self, point: &Wgs84Point) -> bool {
        match self.relation {
            SpatialRelation::Within | SpatialRelation::Intersects => {
                self.shape.contains_point(point)
            }
            SpatialRelation::Disjoint => !self.shape.contains_point(point),
            SpatialRelation::Contains => self.shape == SpatialShape::Point(*point),
        }
    }

    /// Returns the document encoding this predicate applies to.
    #[must_use]
    pub fn encoding(&self) -> &SpatialFieldEncoding {
        &self.encoding
    }

    /// Returns the asserted relation.
    #[must_use]
    pub fn relation(&self) -> SpatialRelation {
        self.relation
    }

    /// Returns the query shape.
    #[must_use]
    pub fn shape(&self) -> &SpatialShape {
        &self.shape
    }

    /// Returns the distance-error percentage.
    #[must_use]
    pub fn distance_error_pct(&self) -> f64 {
        self.distance_error_pct
    }
}

/// The coordinate discretization rule: how many decimal places of a
/// degree spatial key derivation keeps.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct SpatialPrecision(u8);

impl SpatialPrecision {
    /// Validates one precision level: zero to
    /// [`MAX_PRECISION_DECIMALS`] decimal places.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidQuery`] outside that
    /// domain.
    pub fn try_new(decimals: u8) -> Result<Self, RequestValidationError> {
        if decimals > MAX_PRECISION_DECIMALS {
            return Err(RequestValidationError::InvalidQuery);
        }
        Ok(Self(decimals))
    }

    /// Returns the number of decimal places kept.
    #[must_use]
    pub fn decimals(&self) -> u8 {
        self.0
    }

    /// Returns the per-axis cell scale (`10^decimals`).
    fn scale(&self) -> f64 {
        f64::from(10_u32.pow(u32::from(self.0)))
    }
}

impl Default for SpatialPrecision {
    /// The default precision: [`DEFAULT_PRECISION_DECIMALS`] decimal
    /// places.
    fn default() -> Self {
        Self(DEFAULT_PRECISION_DECIMALS)
    }
}

/// One quantized spatial cell: the integer grid cell a point maps to
/// under one [`SpatialPrecision`].
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct QuantizedCell {
    x: i64,
    y: i64,
}

impl QuantizedCell {
    /// Quantizes one point into its cell.
    ///
    /// Both axes are bounded: `(degree + offset) * 10^decimals` is at
    /// most `360 * 10^9` (3.6e11), an exact integer far inside both
    /// `f64` precision and `i64`, so the casts below are lossless on
    /// every accepted input.
    #[must_use]
    pub fn of(point: &Wgs84Point, precision: SpatialPrecision) -> Self {
        #[allow(
            clippy::cast_possible_truncation,
            reason = "the scaled axis is an exact integer of at most 3.6e11 on validated inputs"
        )]
        let x = ((point.longitude.degrees() + 180.0) * precision.scale()).floor() as i64;
        #[allow(
            clippy::cast_possible_truncation,
            reason = "the scaled axis is an exact integer of at most 1.8e11 on validated inputs"
        )]
        let y = ((point.latitude.degrees() + 90.0) * precision.scale()).floor() as i64;
        Self { x, y }
    }

    /// Returns the longitude-axis cell index.
    #[must_use]
    pub fn x(&self) -> i64 {
        self.x
    }

    /// Returns the latitude-axis cell index.
    #[must_use]
    pub fn y(&self) -> i64 {
        self.y
    }
}

#[cfg(test)]
mod tests {
    use super::{
        DEFAULT_DISTANCE_ERROR_PCT, Latitude, Longitude, MAX_DISTANCE_ERROR_PCT, MAX_POLYGON_RINGS,
        MAX_SHAPE_POINTS, MAX_WKT_BYTES, QuantizedCell, SpatialFieldEncoding, SpatialPrecision,
        SpatialPredicate, SpatialRelation, SpatialShape, SpatialShapeError, SpatialUnits,
        Wgs84Point,
    };

    /// Compares two distances with the tolerance double-parsed literals
    /// need; the crate's float discipline avoids `==` on computed floats.
    fn approx(actual: f64, expected: f64) -> bool {
        (actual - expected).abs() <= 1e-9
    }

    fn lon(value: f64) -> Longitude {
        Longitude::try_new(value).expect("valid longitude")
    }

    fn lat(value: f64) -> Latitude {
        Latitude::try_new(value).expect("valid latitude")
    }

    fn point(x: f64, y: f64) -> Wgs84Point {
        Wgs84Point::new(lon(x), lat(y))
    }

    fn encoding() -> SpatialFieldEncoding {
        SpatialFieldEncoding::try_new_point("latitude", "longitude").expect("valid encoding")
    }

    // ---- units and coordinate domain ------------------------------------

    #[test]
    fn longitude_accepts_the_wgs84_domain_and_rejects_outside_it() {
        assert!(Longitude::try_new(-180.0).is_ok());
        assert!(Longitude::try_new(180.0).is_ok());
        assert!(Longitude::try_new(0.0).is_ok());
        for bad in [-180.000_1, 180.000_1, f64::NAN, f64::INFINITY] {
            assert!(Longitude::try_new(bad).is_err(), "longitude {bad} must refuse");
        }
    }

    #[test]
    fn latitude_accepts_the_wgs84_domain_and_rejects_outside_it() {
        assert!(Latitude::try_new(-90.0).is_ok());
        assert!(Latitude::try_new(90.0).is_ok());
        for bad in [-90.000_1, 90.000_1, f64::NAN, f64::NEG_INFINITY] {
            assert!(Latitude::try_new(bad).is_err(), "latitude {bad} must refuse");
        }
    }

    #[test]
    fn coordinate_errors_are_typed() {
        assert_eq!(
            Longitude::try_new(f64::NAN).unwrap_err(),
            SpatialShapeError::CoordinateNotFinite
        );
        assert_eq!(Latitude::try_new(91.0).unwrap_err(), SpatialShapeError::CoordinateOutOfRange);
    }

    #[test]
    fn units_carry_the_v1_nautical_mile_factors() {
        // V1 SpatialUtils.cs:50-67 scales haversine nautical miles by
        // 1.1515 for miles and 1.852 for kilometers.
        assert!(approx(SpatialUnits::Miles.factor_from_nautical_miles(), 1.1515));
        assert!(approx(SpatialUnits::Kilometers.factor_from_nautical_miles(), 1.852));
    }

    // ---- distance math (V1 constants, pinned by hand-computed parity) ---

    #[test]
    fn one_equator_degree_is_sixty_nautical_miles_in_v1_units() {
        let a = point(0.0, 0.0);
        let b = point(1.0, 0.0);
        let km = Wgs84Point::distance(&a, &b, SpatialUnits::Kilometers);
        let miles = Wgs84Point::distance(&a, &b, SpatialUnits::Miles);
        // acos(cos(1 degree)) = 1 degree; 60 NM -> 111.12 km / 69.09 mi.
        assert!((km - 111.12).abs() < 0.01, "km drift: {km}");
        assert!((miles - 69.09).abs() < 0.01, "miles drift: {miles}");
        assert!((a.angular_distance_degrees(&b) - 1.0).abs() < 1e-9);
    }

    #[test]
    fn identical_points_are_exactly_zero_distance() {
        let a = point(12.345_6, -45.678_9);
        assert_eq!(Wgs84Point::distance(&a, &a, SpatialUnits::Kilometers), 0.0);
        assert_eq!(a.angular_distance_degrees(&a), 0.0);
    }

    #[test]
    fn rounding_floors_to_the_round_factor_like_v1() {
        // V1 SpatialUtils.cs:63-67: distance - distance % round, only for
        // round > 0.
        assert!(approx(Wgs84Point::apply_rounding(100.7, 7.0), 98.0));
        assert!(approx(Wgs84Point::apply_rounding(6.4, 7.0), 0.0));
        assert!(approx(Wgs84Point::apply_rounding(100.7, 0.0), 100.7));
        assert!(approx(Wgs84Point::apply_rounding(100.7, -1.0), 100.7));
    }

    #[test]
    fn circle_radius_translates_to_degrees_through_the_earth_mean_radius() {
        // V1 ShapeStringReadWriter.cs:56-62: km / 6371.0087714 * (180/pi),
        // with miles converted by 1.60934 first.
        let km_degrees = SpatialShape::circle_radius_degrees(100.0, SpatialUnits::Kilometers);
        let expected = 100.0 / 6371.008_771_4 * (180.0 / std::f64::consts::PI);
        assert!(approx(km_degrees, expected));
        let miles_degrees = SpatialShape::circle_radius_degrees(50.0, SpatialUnits::Miles);
        let expected_miles = 50.0 * 1.609_34 / 6371.008_771_4 * (180.0 / std::f64::consts::PI);
        assert!(approx(miles_degrees, expected_miles));
    }

    // ---- shape parsing ---------------------------------------------------

    #[test]
    fn parses_a_wkt_point() {
        let shape = SpatialShape::parse("POINT (12.5 -70.2)", SpatialUnits::Kilometers)
            .expect("valid point wkt");
        assert_eq!(shape, SpatialShape::Point(point(12.5, -70.2)));
        assert!(shape.is_point());
        assert!(shape.as_circle().is_none());
    }

    #[test]
    fn wkt_is_case_insensitive_and_whitespace_tolerant() {
        let shape = SpatialShape::parse("  point  ( 1 \t 2 ) ", SpatialUnits::Kilometers)
            .expect("tag case and whitespace are free");
        assert_eq!(shape, SpatialShape::Point(point(1.0, 2.0)));
    }

    #[test]
    fn higher_ordinate_dimensions_are_discarded_like_the_v1_sanitizer() {
        // V1 WktSanitizer.cs:10-37 reduces Z/M/ZM shapes to 2D.
        let flagged = SpatialShape::parse("POINT Z (1 2 3)", SpatialUnits::Kilometers)
            .expect("z flag is reduced, not rejected");
        assert_eq!(flagged, SpatialShape::Point(point(1.0, 2.0)));
        let extra = SpatialShape::parse("POINT (1 2 3 4)", SpatialUnits::Kilometers)
            .expect("extra ordinates are dropped");
        assert_eq!(extra, SpatialShape::Point(point(1.0, 2.0)));
    }

    #[test]
    fn parses_the_v1_circle_form_with_query_units() {
        let shape = SpatialShape::parse("Circle(13.2 50.5 d=100)", SpatialUnits::Miles)
            .expect("circle form parses");
        assert_eq!(
            shape,
            SpatialShape::Circle {
                center: point(13.2, 50.5),
                radius: 100.0,
                units: SpatialUnits::Miles,
            }
        );
        assert!(shape.as_circle().is_some());
        assert!(!shape.is_point());
        assert!(
            SpatialShape::parse("Circle(0 0 d=0)", SpatialUnits::Miles).is_err(),
            "a non-positive radius is not a circle"
        );
    }

    #[test]
    fn parses_linestrings_and_polygons_with_and_without_closure() {
        let line = SpatialShape::parse("LINESTRING (0 0, 1 1, 2 0)", SpatialUnits::Kilometers)
            .expect("linestring");
        match &line {
            SpatialShape::LineString { points } => assert_eq!(points.len(), 3),
            other => panic!("expected a linestring, got {other:?}"),
        }
        let closed = SpatialShape::parse(
            "POLYGON ((0 0, 10 0, 10 10, 0 10, 0 0))",
            SpatialUnits::Kilometers,
        )
        .expect("closed polygon");
        let open =
            SpatialShape::parse("POLYGON ((0 0, 10 0, 10 10, 0 10))", SpatialUnits::Kilometers)
                .expect("open ring closes implicitly");
        assert_eq!(closed, open);
    }

    #[test]
    fn parses_a_polygon_with_a_hole() {
        let shape = SpatialShape::parse(
            "POLYGON ((0 0, 10 0, 10 10, 0 10), (4 4, 6 4, 6 6, 4 6))",
            SpatialUnits::Kilometers,
        )
        .expect("polygon with hole");
        match &shape {
            SpatialShape::Polygon(polygon) => {
                assert_eq!(polygon.exterior().len(), 4);
                assert_eq!(polygon.holes().len(), 1);
            }
            other => panic!("expected a polygon, got {other:?}"),
        }
    }

    #[test]
    fn parses_the_v1_rectangle_shorthand_as_an_envelope() {
        // V1 ShapeStringConverter.cs:47-55 lowers BOX(2D)(...) to the bare
        // "x1 y1 x2 y2" rectangle form WktSanitizer recognizes.
        let bare =
            SpatialShape::parse("0 0 10 10", SpatialUnits::Kilometers).expect("bare rectangle");
        let box2d = SpatialShape::parse("BOX2D(0 0, 10 10)", SpatialUnits::Kilometers)
            .expect("box form lowers to the same envelope");
        let swapped = SpatialShape::parse("BOX(10 10, 0 0)", SpatialUnits::Kilometers)
            .expect("corner order never matters");
        assert_eq!(bare, box2d);
        assert_eq!(bare, swapped);
        assert_eq!(
            bare,
            SpatialShape::Envelope {
                west: lon(0.0),
                south: lat(0.0),
                east: lon(10.0),
                north: lat(10.0),
            }
        );
    }

    #[test]
    fn malformed_and_out_of_domain_wkt_fail_with_typed_errors() {
        assert_eq!(
            SpatialShape::parse("", SpatialUnits::Kilometers).unwrap_err(),
            SpatialShapeError::Empty
        );
        assert_eq!(
            SpatialShape::parse("   ", SpatialUnits::Kilometers).unwrap_err(),
            SpatialShapeError::Empty
        );
        assert_eq!(
            SpatialShape::parse("POLYHEDRALSURFACE ((0 0, 1 1))", SpatialUnits::Kilometers)
                .unwrap_err(),
            SpatialShapeError::Unsupported
        );
        assert_eq!(
            SpatialShape::parse("POINT (a b)", SpatialUnits::Kilometers).unwrap_err(),
            SpatialShapeError::Malformed
        );
        assert_eq!(
            SpatialShape::parse("POINT (1e5 2)", SpatialUnits::Kilometers).unwrap_err(),
            SpatialShapeError::Malformed
        );
        assert_eq!(
            SpatialShape::parse("POINT (200 0)", SpatialUnits::Kilometers).unwrap_err(),
            SpatialShapeError::CoordinateOutOfRange
        );
        assert_eq!(
            SpatialShape::parse("POINT (0 100)", SpatialUnits::Kilometers).unwrap_err(),
            SpatialShapeError::CoordinateOutOfRange
        );
        assert_eq!(
            SpatialShape::parse("POINT (1)", SpatialUnits::Kilometers).unwrap_err(),
            SpatialShapeError::Malformed
        );
        assert_eq!(
            SpatialShape::parse("POINT (1 2) junk", SpatialUnits::Kilometers).unwrap_err(),
            SpatialShapeError::Malformed
        );
        assert_eq!(
            SpatialShape::parse("POLYGON ((0 0, 1 0))", SpatialUnits::Kilometers).unwrap_err(),
            SpatialShapeError::Malformed
        );
        assert_eq!(
            SpatialShape::parse("LINESTRING (0 0)", SpatialUnits::Kilometers).unwrap_err(),
            SpatialShapeError::Malformed
        );
    }

    #[test]
    fn wkt_inputs_beyond_the_bounds_fail_with_typed_errors() {
        let padded = format!("POINT (1 2{}x)", " ".repeat(MAX_WKT_BYTES));
        assert_eq!(
            SpatialShape::parse(&padded, SpatialUnits::Kilometers).unwrap_err(),
            SpatialShapeError::TooLarge
        );
        let mut ring = String::from("POLYGON ((0 0");
        for index in 0..=MAX_SHAPE_POINTS {
            let x = index as f64 * 0.001;
            ring.push_str(&format!(", {x} 0.0"));
        }
        ring.push_str("))");
        assert_eq!(
            SpatialShape::parse(&ring, SpatialUnits::Kilometers).unwrap_err(),
            SpatialShapeError::TooManyPoints
        );
        let mut holes = String::from("POLYGON ((0 0, 1 0, 1 1, 0 1)");
        for ring_index in 0..MAX_POLYGON_RINGS {
            let base = 0.1 + ring_index as f64 * 0.01;
            let plus = base + 0.001;
            holes.push_str(&format!(
                ", ({base} {base}, {plus} {base}, {plus} {plus}, {base} {plus})"
            ));
        }
        holes.push(')');
        assert_eq!(
            SpatialShape::parse(&holes, SpatialUnits::Kilometers).unwrap_err(),
            SpatialShapeError::TooManyRings
        );
    }

    // ---- typed predicate -------------------------------------------------

    #[test]
    fn the_default_distance_error_pct_is_the_v1_default() {
        assert!(approx(DEFAULT_DISTANCE_ERROR_PCT, 0.025));
        assert!(approx(MAX_DISTANCE_ERROR_PCT, 0.5));
    }

    #[test]
    fn distance_error_pct_outside_the_v1_domain_refuses() {
        let shape = SpatialShape::Point(point(0.0, 0.0));
        for bad in [-0.1, 0.6, f64::NAN] {
            assert!(
                SpatialPredicate::try_new(encoding(), SpatialRelation::Within, shape.clone(), bad)
                    .is_err(),
                "error pct {bad} must refuse"
            );
        }
        for good in [0.0, MAX_DISTANCE_ERROR_PCT] {
            assert!(
                SpatialPredicate::try_new(encoding(), SpatialRelation::Within, shape.clone(), good)
                    .is_ok(),
                "error pct {good} must be valid"
            );
        }
        let predicate =
            SpatialPredicate::with_default_error_pct(encoding(), SpatialRelation::Within, shape)
                .expect("default error pct is valid");
        assert!(approx(predicate.distance_error_pct(), DEFAULT_DISTANCE_ERROR_PCT));
    }

    #[test]
    fn an_encoding_with_invalid_fields_refuses() {
        assert!(
            SpatialFieldEncoding::try_new_point(" ", "longitude").is_err(),
            "a blank field name must refuse"
        );
        assert!(SpatialFieldEncoding::try_new_wkt("").is_err(), "an empty field name must refuse");
    }

    // ---- predicate evaluation over point documents -----------------------

    fn circle_around(x: f64, y: f64, radius: f64, units: SpatialUnits) -> SpatialShape {
        SpatialShape::Circle { center: point(x, y), radius, units }
    }

    #[test]
    fn a_point_inside_the_circle_radius_matches_within() {
        // ~55 km north of the center, inside a 100 km circle.
        let shape = circle_around(10.0, 10.0, 100.0, SpatialUnits::Kilometers);
        let near = point(10.0, 10.5);
        let far = point(10.0, 12.0);
        let within = SpatialPredicate::with_default_error_pct(
            encoding(),
            SpatialRelation::Within,
            shape.clone(),
        )
        .unwrap();
        assert!(within.matches_point(&near));
        assert!(!within.matches_point(&far));
        let intersects = SpatialPredicate::with_default_error_pct(
            encoding(),
            SpatialRelation::Intersects,
            shape,
        )
        .unwrap();
        assert!(intersects.matches_point(&near));
    }

    #[test]
    fn disjoint_is_the_negation_of_within_for_point_documents() {
        let shape = circle_around(0.0, 0.0, 10.0, SpatialUnits::Kilometers);
        let near = point(0.0, 0.05);
        let far = point(0.0, 5.0);
        let disjoint =
            SpatialPredicate::with_default_error_pct(encoding(), SpatialRelation::Disjoint, shape)
                .unwrap();
        assert!(!disjoint.matches_point(&near));
        assert!(disjoint.matches_point(&far));
    }

    #[test]
    fn contains_requires_an_exactly_equal_point_shape() {
        let target = point(3.0, 4.0);
        let equal = SpatialPredicate::with_default_error_pct(
            encoding(),
            SpatialRelation::Contains,
            SpatialShape::Point(target),
        )
        .unwrap();
        assert!(equal.matches_point(&target));
        assert!(!equal.matches_point(&point(3.0, 4.000_1)));
        // A point document cannot contain an extent: V1 Corax collapses
        // Contains onto Within (SpatialMatch.cs:161-162); this foundation
        // keeps the typed vocabulary honest and records the divergence.
        let inside_circle = SpatialPredicate::with_default_error_pct(
            encoding(),
            SpatialRelation::Contains,
            circle_around(0.0, 0.0, 500.0, SpatialUnits::Kilometers),
        )
        .unwrap();
        assert!(!inside_circle.matches_point(&point(0.0, 0.0)));
    }

    #[test]
    fn envelope_membership_is_boundary_inclusive() {
        let shape = SpatialShape::Envelope {
            west: lon(0.0),
            south: lat(0.0),
            east: lon(10.0),
            north: lat(10.0),
        };
        let within =
            SpatialPredicate::with_default_error_pct(encoding(), SpatialRelation::Within, shape)
                .unwrap();
        assert!(within.matches_point(&point(5.0, 5.0)));
        assert!(within.matches_point(&point(0.0, 0.0)));
        assert!(within.matches_point(&point(10.0, 10.0)));
        assert!(!within.matches_point(&point(10.000_1, 5.0)));
    }

    #[test]
    fn an_envelope_wrapping_the_antimeridian_spans_both_sides() {
        let shape = SpatialShape::Envelope {
            west: lon(170.0),
            south: lat(-10.0),
            east: lon(-170.0),
            north: lat(10.0),
        };
        let within =
            SpatialPredicate::with_default_error_pct(encoding(), SpatialRelation::Within, shape)
                .unwrap();
        assert!(within.matches_point(&point(180.0, 0.0)));
        assert!(within.matches_point(&point(-175.0, 5.0)));
        assert!(!within.matches_point(&point(0.0, 0.0)));
    }

    #[test]
    fn polygon_membership_counts_edges_and_holes() {
        let shape = SpatialShape::parse(
            "POLYGON ((0 0, 10 0, 10 10, 0 10), (4 4, 6 4, 6 6, 4 6))",
            SpatialUnits::Kilometers,
        )
        .expect("polygon with hole");
        let within =
            SpatialPredicate::with_default_error_pct(encoding(), SpatialRelation::Within, shape)
                .unwrap();
        assert!(within.matches_point(&point(1.0, 1.0)), "solid interior");
        assert!(!within.matches_point(&point(5.0, 5.0)), "inside the hole");
        assert!(within.matches_point(&point(4.0, 4.0)), "on the hole edge");
        assert!(within.matches_point(&point(0.0, 5.0)), "on the exterior edge");
        assert!(!within.matches_point(&point(-0.001, 5.0)), "just outside");
    }

    #[test]
    fn a_linestring_matches_only_points_on_its_segments() {
        let shape = SpatialShape::parse("LINESTRING (0 0, 10 10)", SpatialUnits::Kilometers)
            .expect("linestring");
        let within =
            SpatialPredicate::with_default_error_pct(encoding(), SpatialRelation::Within, shape)
                .unwrap();
        assert!(within.matches_point(&point(5.0, 5.0)));
        assert!(within.matches_point(&point(0.0, 0.0)));
        assert!(within.matches_point(&point(10.0, 10.0)));
        assert!(!within.matches_point(&point(5.0, 5.000_1)));
    }

    #[test]
    fn evaluation_of_the_worst_bounded_shape_completes() {
        // The largest accepted polygon exercises the bounded evaluation
        // path without panic: MAX_SHAPE_POINTS positions in the ring,
        // sweeping the north edge and closing along the south so the
        // origin is inside the covered area.
        let mut exterior = String::from("POLYGON ((-90 -89");
        for index in 0..(MAX_SHAPE_POINTS - 2) {
            let x = -90.0 + 177.0 * (index as f64 / MAX_SHAPE_POINTS as f64);
            exterior.push_str(&format!(", {x} 89.0"));
        }
        exterior.push_str(", 87 -89");
        exterior.push_str("))");
        let shape = SpatialShape::parse(&exterior, SpatialUnits::Kilometers)
            .expect("maximal bounded polygon parses");
        let predicate =
            SpatialPredicate::with_default_error_pct(encoding(), SpatialRelation::Within, shape)
                .unwrap();
        assert!(predicate.matches_point(&point(0.0, 0.0)));
        assert!(!predicate.matches_point(&point(0.0, 89.5)));
    }

    // ---- precision / discretization ---------------------------------------

    #[test]
    fn precision_is_bounded_to_nine_decimals_with_a_default() {
        assert_eq!(SpatialPrecision::default().decimals(), 6);
        assert!(SpatialPrecision::try_new(0).is_ok());
        assert!(SpatialPrecision::try_new(9).is_ok());
        assert!(SpatialPrecision::try_new(10).is_err());
    }

    #[test]
    fn quantization_is_monotone_and_idempotent() {
        let precision = SpatialPrecision::default();
        let a = point(12.345_678_5, 45.678_912_5);
        let near = point(12.345_678_9, 45.678_912_1);
        let far = point(12.35, 45.68);
        let cell_a = a.quantize(precision);
        assert_eq!(cell_a, near.quantize(precision));
        assert_ne!(cell_a, far.quantize(precision));
        assert_eq!(cell_a, QuantizedCell::of(&a, precision));
        // Points that share a cell quantize identically: the map is
        // idempotent in the sense that any two preimages of one cell are
        // indistinguishable after quantization.
        assert_eq!(
            point(12.345_678_0, 45.678_912_0).quantize(precision),
            point(12.345_678_7, 45.678_912_7).quantize(precision)
        );
    }

    #[test]
    fn quantization_covers_the_domain_floor_aligned() {
        let precision = SpatialPrecision::try_new(6).expect("valid precision");
        let origin = point(-180.0, -90.0).quantize(precision);
        assert_eq!(origin.x(), 0);
        assert_eq!(origin.y(), 0);
        let corner = point(180.0, 90.0).quantize(precision);
        assert_eq!(corner.x(), 360_000_000);
        assert_eq!(corner.y(), 180_000_000);
    }

    // ---- document field encoding ------------------------------------------

    #[test]
    fn field_encoding_renders_the_v1_dynamic_field_tokens() {
        let point_encoding = encoding();
        assert_eq!(point_encoding.to_v1_field_token(), "spatial.point(latitude, longitude)");
        let wkt_encoding = SpatialFieldEncoding::try_new_wkt("shape").expect("valid wkt encoding");
        assert_eq!(wkt_encoding.to_v1_field_token(), "spatial.wkt(shape)");
        assert!(wkt_encoding.is_wkt());
        assert!(!point_encoding.is_wkt());
    }

    // ---- predicate accessors ----------------------------------------------

    #[test]
    fn the_predicate_exposes_its_parts_read_only() {
        let predicate = SpatialPredicate::with_default_error_pct(
            encoding(),
            SpatialRelation::Disjoint,
            circle_around(0.0, 0.0, 10.0, SpatialUnits::Miles),
        )
        .unwrap();
        assert_eq!(predicate.relation(), SpatialRelation::Disjoint);
        assert!(approx(predicate.distance_error_pct(), 0.025));
        assert!(predicate.shape().as_circle().is_some());
        assert!(!predicate.shape().is_point());
        assert_eq!(predicate.encoding(), &encoding());
    }
}
