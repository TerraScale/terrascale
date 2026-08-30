//! Public-path analyzer-foundation tests (GAP-Q13).
//!
//! The analyzer and term-vector foundation is a contract seam, not an
//! engine: these tests pin what any caller can observe — the closed
//! analyzer set and its fail-closed resolution, the sandbox bounds, the
//! versioned term-vector contract, and the V1 wire names of the
//! term-vector modes. Nothing here exercises search, scoring, or an index;
//! those do not exist on this seam yet.

use terrabase::query::{
    ANALYZER_LANGUAGE_PREFIX, ANALYZER_LANGUAGE_VERSION, Analyzer, AnalyzerError, AnalyzerKind,
    MAX_ANALYZED_VALUE_BYTES, MAX_TOKENS_PER_FIELD,
};

#[test]
fn v1_default_analyzer_names_resolve_and_unknown_names_fail_closed() {
    // V1's three engine-default analyzer names, verbatim at the pin.
    for (name, identity) in [
        ("KeywordAnalyzer", "KeywordAnalyzer"),
        ("LowerCaseKeywordAnalyzer", "LowerCaseKeywordAnalyzer"),
        ("TerraBaseStandardAnalyzer", "TerraBaseStandardAnalyzer"),
    ] {
        let analyzer = Analyzer::resolve(name).expect("V1 default name must resolve");
        assert_eq!(analyzer.identity(), identity);
    }

    // The closed set refuses arbitrary and custom analyzer names.
    for name in [
        "SimpleAnalyzer",
        "StopAnalyzer",
        "KeywordTokenizer",
        "MyAnalyzer",
    ] {
        assert_eq!(
            Analyzer::resolve(name),
            Err(AnalyzerError::UnknownAnalyzer),
            "{name} must fail closed, never fall back to equality"
        );
    }
}

#[test]
fn the_sandbox_bounds_are_observable_on_the_public_path() {
    let analyzer = Analyzer::resolve("LowerCaseKeywordAnalyzer").unwrap();
    let over_input = "a".repeat(MAX_ANALYZED_VALUE_BYTES + 1);
    assert_eq!(
        analyzer.analyze("body", &over_input, terrabase::query::TermVectorMode::Frequencies),
        Err(AnalyzerError::InputTooLarge)
    );

    let whitespace = Analyzer::try_new(
        AnalyzerKind::compose(terrabase::query::TokenizerKind::Whitespace, Vec::new()).unwrap(),
    )
    .unwrap();
    let over_tokens = "a ".repeat(MAX_TOKENS_PER_FIELD + 1);
    assert_eq!(
        whitespace.analyze("body", &over_tokens, terrabase::query::TermVectorMode::Frequencies),
        Err(AnalyzerError::TokenBoundExceeded)
    );
}

#[test]
fn the_term_vector_contract_is_versioned_and_fails_closed_on_other_versions() {
    use terrabase::query::{TermVector, TermVectorEntry, TermVectorMode};

    let entry = TermVectorEntry::try_new("term".to_owned(), 1, Vec::new(), Vec::new())
        .expect("valid entry");
    let vector = TermVector::try_new("body", TermVectorMode::Frequencies, vec![entry])
        .expect("valid vector");
    assert_eq!(vector.contract_version(), ANALYZER_LANGUAGE_VERSION);
    assert!(vector.contract_version().starts_with(ANALYZER_LANGUAGE_PREFIX));

    let entry = TermVectorEntry::try_new("term".to_owned(), 1, Vec::new(), Vec::new())
        .expect("valid entry");
    for version in ["analyzer/0.9", "analyzer/2.0", "idx/1.0", ""] {
        assert_eq!(
            TermVector::try_from_parts(
                version,
                "body",
                TermVectorMode::Frequencies,
                vec![entry.clone()]
            ),
            Err(AnalyzerError::UnknownVersion),
            "version {version:?} must be refused before any other validation"
        );
    }
}

#[test]
fn term_vector_modes_round_trip_the_v1_wire_names() {
    use terrabase::query::TermVectorMode;

    for (token, mode) in [
        ("No", TermVectorMode::No),
        ("Yes", TermVectorMode::Frequencies),
        ("WithPositions", TermVectorMode::WithPositions),
        ("WithOffsets", TermVectorMode::WithOffsets),
        ("WithPositionsAndOffsets", TermVectorMode::WithPositionsAndOffsets),
    ] {
        assert_eq!(TermVectorMode::parse(token), Ok(mode));
        assert_eq!(mode.wire_name(), token);
    }
    assert_eq!(
        TermVectorMode::parse("with-offsets"),
        Err(AnalyzerError::UnknownAnalyzer),
        "an unknown mode fails closed rather than degrading to a weaker vector"
    );
}
