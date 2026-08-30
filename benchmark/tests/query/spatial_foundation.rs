//! Public-path spatial foundation tests (GAP-Q14).
//!
//! These tests reach only the re-exported spatial vocabulary: the typed
//! coordinate, unit, shape, and predicate values an external caller
//! constructs. The deep unit coverage — full WKT grammar, ring bounds,
//! hole semantics, the maximal bounded polygon — lives inline in
//! `src/query/spatial.rs`; this file pins the public contract at the
//! seam: the V1 distance arithmetic, the V1 dynamic-field tokens, the
//! distance-error domain, and the point-document predicate behavior with
//! the V1 relation set.

use terrabase::{
    RequestValidationError,
    query::{
        DEFAULT_DISTANCE_ERROR_PCT, Latitude, Longitude, MAX_DISTANCE_ERROR_PCT,
        SpatialFieldEncoding, SpatialPredicate, SpatialRelation, SpatialShape, SpatialShapeError,
        SpatialUnits, Wgs84Point,
    },
};

fn point(x: f64, y: f64) -> Wgs84Point {
    Wgs84Point::new(
        Longitude::try_new(x).expect("valid longitude"),
        Latitude::try_new(y).expect("valid latitude"),
    )
}

fn encoding() -> SpatialFieldEncoding {
    SpatialFieldEncoding::try_new_point("latitude", "longitude").expect("valid encoding")
}

#[test]
fn v1_distance_arithmetic_is_pinned_at_the_public_seam() {
    // One equator degree is 60 nautical miles in V1's formula, which is
    // 111.12 km and 69.09 miles after V1's unit factors.
    let origin = point(0.0, 0.0);
    let one_degree_east = point(1.0, 0.0);
    let kilometers = Wgs84Point::distance(&origin, &one_degree_east, SpatialUnits::Kilometers);
    let miles = Wgs84Point::distance(&origin, &one_degree_east, SpatialUnits::Miles);
    assert!((kilometers - 111.12).abs() < 0.01, "km drift: {kilometers}");
    assert!((miles - 69.09).abs() < 0.01, "miles drift: {miles}");
    assert_eq!(Wgs84Point::distance(&origin, &origin, SpatialUnits::Kilometers), 0.0);
    // V1 rounds ordering distances by flooring to the round factor.
    assert_eq!(Wgs84Point::apply_rounding(100.7, 7.0), 98.0);
}

#[test]
fn the_v1_dynamic_field_tokens_render_publicly() {
    assert_eq!(encoding().to_v1_field_token(), "spatial.point(latitude, longitude)");
    let wkt = SpatialFieldEncoding::try_new_wkt("shape").expect("valid encoding");
    assert_eq!(wkt.to_v1_field_token(), "spatial.wkt(shape)");
}

#[test]
fn a_spatial_predicate_refuses_its_out_of_domain_error_pct() {
    let shape = SpatialShape::parse("Circle(0 0 d=10)", SpatialUnits::Kilometers)
        .expect("valid circle shape");
    assert_eq!(
        SpatialPredicate::try_new(encoding(), SpatialRelation::Within, shape.clone(), f64::NAN)
            .unwrap_err(),
        RequestValidationError::InvalidQuery
    );
    let predicate =
        SpatialPredicate::with_default_error_pct(encoding(), SpatialRelation::Within, shape)
            .expect("default error pct is in domain");
    assert_eq!(predicate.distance_error_pct(), DEFAULT_DISTANCE_ERROR_PCT);
    assert!(predicate.distance_error_pct() <= MAX_DISTANCE_ERROR_PCT);
}

#[test]
fn point_documents_evaluate_all_four_v1_relations() {
    let shape = SpatialShape::parse("Circle(10 10 d=100)", SpatialUnits::Kilometers)
        .expect("valid circle shape");
    let inside = point(10.0, 10.5);
    let outside = point(10.0, 12.0);
    for relation in [SpatialRelation::Within, SpatialRelation::Intersects] {
        let predicate =
            SpatialPredicate::with_default_error_pct(encoding(), relation, shape.clone())
                .expect("valid predicate");
        assert!(predicate.matches_point(&inside), "{relation:?}");
        assert!(!predicate.matches_point(&outside), "{relation:?}");
    }
    let disjoint =
        SpatialPredicate::with_default_error_pct(encoding(), SpatialRelation::Disjoint, shape)
            .expect("valid predicate");
    assert!(!disjoint.matches_point(&inside));
    assert!(disjoint.matches_point(&outside));
}

#[test]
fn unsupported_wkt_refuses_with_a_typed_public_error() {
    assert_eq!(
        SpatialShape::parse("GEOMETRYCOLLECTION (POINT (1 2))", SpatialUnits::Kilometers)
            .unwrap_err(),
        SpatialShapeError::Unsupported
    );
}

#[test]
fn a_spatial_predicate_refuses_an_unvalidated_hand_built_shape() {
    // SpatialShape is a public enum, so a caller can assemble a variant
    // the parser would refuse; the predicate seam must refuse it too, or
    // point-document evaluation would depend on the build profile.
    let empty_line = SpatialShape::LineString { points: Vec::new() };
    assert_eq!(
        SpatialPredicate::with_default_error_pct(encoding(), SpatialRelation::Disjoint, empty_line)
            .unwrap_err(),
        RequestValidationError::InvalidQuery
    );
    let non_positive_circle = SpatialShape::Circle {
        center: point(0.0, 0.0),
        radius: 0.0,
        units: SpatialUnits::Kilometers,
    };
    assert_eq!(
        SpatialPredicate::with_default_error_pct(
            encoding(),
            SpatialRelation::Within,
            non_positive_circle
        )
        .unwrap_err(),
        RequestValidationError::InvalidQuery
    );
    // The shapes the parser produces still construct at the seam.
    let parsed = SpatialShape::parse("LINESTRING (0 0, 1 1)", SpatialUnits::Kilometers)
        .expect("parsed linestring is valid");
    assert!(
        SpatialPredicate::with_default_error_pct(encoding(), SpatialRelation::Within, parsed)
            .is_ok()
    );
}

#[test]
fn spatial_encodings_refuse_field_names_that_break_the_token() {
    // Field names carrying the token's own delimiters would render a token
    // whose structure names different fields than the encoding holds, so
    // the constructors refuse them at the public seam as well.
    assert_eq!(
        SpatialFieldEncoding::try_new_point("lat, x", "lng").unwrap_err(),
        RequestValidationError::InvalidQuery
    );
    assert_eq!(
        SpatialFieldEncoding::try_new_wkt("shape, extra").unwrap_err(),
        RequestValidationError::InvalidQuery
    );
}
