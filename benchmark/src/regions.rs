//! Canonical Region identity: the [`RegionCode`] newtype and the
//! production [`RegionCatalog`].
//!
//! A Region has no ULID. Its only internal and public identity is a strict
//! uppercase three-letter airport code (GOAL.md "Canonical Region identity
//! and OCI mapping"; ADR 0033 "Canonical Region identity"). The engine and
//! the private TBEP carry the value as the typed fixed three-byte ASCII
//! form [`RegionCode::as_bytes`]; REST carries the
//! same three characters as a string via [`RegionCode::as_str`] and
//! [`std::fmt::Display`].
//!
//! Parsers reject lowercase text, wrong lengths, non-ASCII text, and
//! unregistered codes, and they never normalize input: `"gru"` is a typed
//! refusal, never `GRU`. No alias exists anywhere in the engine — a
//! provider-labeled airport code for a cataloged location is refused
//! exactly like every other unregistered code. Provider Region names,
//! realm names, availability and fault domains, OCIDs, compartments, and
//! VCN identifiers never appear in this module or any other engine code:
//! the signed, versioned provider mapping lives only in the Orchestrator
//! provider adapter.
//!
//! The initial production catalog is exactly `GRU`, `SFO`, and `FRA`.
//! The architecture ceiling is [`REGION_CEILING`] active Regions; the
//! engine-side admission seam enforces the ceiling and the
//! retired-code invariant (a retired code stays reserved forever and can
//! never be admitted again). The admission qualification that must pass
//! before a new Region is exposed lives outside the engine, in the
//! Orchestrator and product lanes (gap-sweep row 35).

use std::fmt;
use std::str::FromStr;

/// The fixed wire width of a [`RegionCode`]: exactly three ASCII bytes.
pub const REGION_CODE_BYTES: usize = 3;

/// The architecture ceiling on the number of active Regions in one
/// catalog generation. Reserved (retired) codes are not Regions and do
/// not count against it.
pub const REGION_CEILING: usize = 20;

/// A canonical Region identity: exactly three strict uppercase ASCII
/// letters (`A`..=`Z`).
///
/// The type enforces only the shape. Whether a value names an admitted
/// Region is a [`RegionCatalog`] question, so callers that accept Region
/// identities parse through a catalog ([`RegionCatalog::parse`]), never
/// through [`RegionCode::from_text`] alone.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct RegionCode([u8; REGION_CODE_BYTES]);

/// The typed failures of Region code parsing.
///
/// Every variant names its exact rejection class; none of them carries or
/// implies a repaired value, because parsers never normalize input.
#[derive(Clone, Copy, Debug, Eq, PartialEq, thiserror::Error)]
pub enum RegionCodeError {
    /// The input was not exactly [`REGION_CODE_BYTES`] bytes long. Carries
    /// the observed byte length, counting multi-byte UTF-8 characters one
    /// byte per encoded unit.
    #[error("Region code must be exactly three bytes (got {0})")]
    WrongLength(usize),
    /// The input contained a byte at or above `0x80`, so it is not ASCII
    /// text. Carries the index of the first such byte.
    #[error("Region code contains non-ASCII byte at index {index}")]
    NonAscii {
        /// The index of the first non-ASCII byte.
        index: usize,
    },
    /// The input contained an ASCII byte that is not one of the strict
    /// uppercase letters `A`..=`Z` — lowercase text, a digit, punctuation,
    /// whitespace, or a control byte. Carries the index and value of the
    /// first such byte.
    #[error("Region code byte at index {index} is not an uppercase ASCII letter (0x{byte:02x})")]
    NotUppercaseLetter {
        /// The index of the offending byte.
        index: usize,
        /// The offending byte itself.
        byte: u8,
    },
    /// The text is a well-formed code that this catalog has never
    /// admitted. Carries the refused code. Alias attempts land here: the
    /// engine keeps no alias table.
    #[error("Region code {0} is not registered")]
    Unregistered(RegionCode),
    /// The text is a well-formed code that this catalog retired. Retired
    /// codes stay reserved forever and can never be admitted again, so
    /// parsing them can never succeed. Carries the retired code.
    #[error("Region code {0} is retired")]
    Retired(RegionCode),
}

/// The typed failures of Region catalog admission and retirement.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RegionAdmissionError {
    /// The code is already active in this catalog. Carries the code.
    AlreadyActive(RegionCode),
    /// The code names a retired Region. A retired code remains reserved
    /// and can never again name a Region, let alone a different
    /// geography. Carries the code.
    Reserved(RegionCode),
    /// Admitting the code would exceed [`REGION_CEILING`] active Regions.
    /// Carries the active count at refusal and the ceiling.
    CeilingExceeded {
        /// The number of active Regions when the refusal happened.
        active: usize,
        /// The ceiling that was about to be exceeded.
        ceiling: usize,
    },
    /// The code is not active in this catalog, so there is nothing to
    /// retire. Carries the code.
    NotActive(RegionCode),
}

impl RegionCode {
    /// `GRU` — a Region of the initial production catalog.
    pub const GRU: Self = Self(*b"GRU");
    /// `SFO` — a Region of the initial production catalog.
    pub const SFO: Self = Self(*b"SFO");
    /// `FRA` — a Region of the initial production catalog.
    pub const FRA: Self = Self(*b"FRA");

    /// Validates the shape of Region code text without consulting any
    /// catalog: exactly three bytes, each a strict uppercase ASCII letter.
    ///
    /// # Errors
    ///
    /// Returns [`RegionCodeError::WrongLength`] for any byte length other
    /// than [`REGION_CODE_BYTES`], [`RegionCodeError::NonAscii`] for the
    /// first byte at or above `0x80`, and
    /// [`RegionCodeError::NotUppercaseLetter`] for the first ASCII byte
    /// outside `A`..=`Z` (lowercase, digit, punctuation, whitespace, or
    /// control). Length is checked before content.
    pub fn from_text(text: &str) -> Result<Self, RegionCodeError> {
        Self::from_shape(text.as_bytes())
    }

    /// Validates the shape of the fixed three-byte wire form without
    /// consulting any catalog. This is the constructor for decoded private
    /// TBEP fields; bytes need not be valid UTF-8, because the shape rules
    /// are defined over bytes.
    ///
    /// # Errors
    ///
    /// Returns [`RegionCodeError::NonAscii`] for the first byte at or
    /// above `0x80` and [`RegionCodeError::NotUppercaseLetter`] for the
    /// first ASCII byte outside `A`..=`Z`.
    pub fn from_wire_bytes(bytes: [u8; REGION_CODE_BYTES]) -> Result<Self, RegionCodeError> {
        Self::from_shape(&bytes)
    }

    /// Returns the typed fixed three-byte ASCII value the engine and the
    /// private TBEP carry.
    #[must_use]
    pub const fn as_bytes(&self) -> [u8; REGION_CODE_BYTES] {
        self.0
    }

    /// Returns the same three characters as a string — the form REST and
    /// public REST carries.
    ///
    /// # Panics
    ///
    /// Never panics in practice: every constructor guarantees three ASCII
    /// letters, so the UTF-8 conversion is infallible.
    #[must_use]
    pub fn as_str(&self) -> &str {
        std::str::from_utf8(&self.0).expect("a RegionCode holds three ASCII letters")
    }

    fn from_shape(bytes: &[u8]) -> Result<Self, RegionCodeError> {
        if bytes.len() != REGION_CODE_BYTES {
            return Err(RegionCodeError::WrongLength(bytes.len()));
        }
        for (index, &byte) in bytes.iter().enumerate() {
            if byte >= 0x80 {
                return Err(RegionCodeError::NonAscii { index });
            }
            if !byte.is_ascii_uppercase() {
                return Err(RegionCodeError::NotUppercaseLetter { index, byte });
            }
        }
        Ok(Self([bytes[0], bytes[1], bytes[2]]))
    }
}

impl fmt::Display for RegionCode {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(self.as_str())
    }
}

impl FromStr for RegionCode {
    type Err = RegionCodeError;

    /// Parses Region code text against the production catalog. This is the
    /// convenient form for adapter code; seam code that owns a catalog
    /// generation should call [`RegionCatalog::parse`] on that catalog
    /// instead.
    fn from_str(text: &str) -> Result<Self, Self::Err> {
        RegionCatalog::production().parse(text)
    }
}

/// One catalog generation of Regions: the active set a database can
/// select, plus the reserved set of retired codes.
///
/// The production catalog is exactly [`Self::INITIAL_ACTIVE`]. Expanding it
/// is an Orchestrator-and-product decision gated on the ADR 0033 admission
/// qualification; the engine-side seam ([`Self::admit`],
/// [`Self::retire`]) records that decision and enforces the
/// retired-code-forever invariant and the [`REGION_CEILING`] ceiling.
/// Provider mappings never enter this type — one catalog generation
/// assigns a code to at most one active Region, and which provider
/// Region that is lives in the Orchestrator provider adapter.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RegionCatalog {
    active: Vec<RegionCode>,
    reserved: Vec<RegionCode>,
}

impl RegionCatalog {
    /// The initial production Regions in catalog order. Regions beyond
    /// these three are not selectable at first production release.
    pub const INITIAL_ACTIVE: [RegionCode; 3] = [RegionCode::GRU, RegionCode::SFO, RegionCode::FRA];

    /// Returns the initial production catalog: `GRU`, `SFO`, `FRA`
    /// active, nothing reserved.
    #[must_use]
    pub fn production() -> Self {
        Self { active: Self::INITIAL_ACTIVE.to_vec(), reserved: Vec::new() }
    }

    /// Returns the active Regions in catalog (admission) order.
    #[must_use]
    pub fn active(&self) -> &[RegionCode] {
        &self.active
    }

    /// Returns the reserved (retired) codes, in retirement order.
    #[must_use]
    pub fn reserved(&self) -> &[RegionCode] {
        &self.reserved
    }

    /// Whether the code names an active Region of this catalog.
    #[must_use]
    pub fn is_registered(&self, code: &RegionCode) -> bool {
        self.active.contains(code)
    }

    /// Whether the code was retired from this catalog. Reserved codes stay
    /// reserved forever.
    #[must_use]
    pub fn is_reserved(&self, code: &RegionCode) -> bool {
        self.reserved.contains(code)
    }

    /// The charter parser: validates the code shape and requires that the
    /// code name an active Region of this catalog. Input is never
    /// normalized — lowercase, padded, aliased, non-ASCII, wrong-length,
    /// unregistered, and retired input are all typed refusals.
    ///
    /// # Errors
    ///
    /// Returns the [`RegionCodeError`] of the first failed rule: shape
    /// first ([`RegionCodeError::WrongLength`],
    /// [`RegionCodeError::NonAscii`], [`RegionCodeError::NotUppercaseLetter`]),
    /// then registration ([`RegionCodeError::Unregistered`] or
    /// [`RegionCodeError::Retired`]).
    pub fn parse(&self, text: &str) -> Result<RegionCode, RegionCodeError> {
        let code = RegionCode::from_text(text)?;
        self.check_registration(code)
    }

    /// The same parser over the typed fixed three-byte wire form, for
    /// decoded private TBEP fields. Input is never normalized.
    ///
    /// # Errors
    ///
    /// Returns the same [`RegionCodeError`] classes as [`Self::parse`].
    pub fn parse_wire_bytes(
        &self,
        bytes: [u8; REGION_CODE_BYTES],
    ) -> Result<RegionCode, RegionCodeError> {
        let code = RegionCode::from_wire_bytes(bytes)?;
        self.check_registration(code)
    }

    /// Records one Region admission into this catalog generation. The
    /// admission qualification itself (ADR 0033: availability, capacity,
    /// price, benchmark evidence, and the product lanes) happens before
    /// this call and outside the engine.
    ///
    /// # Errors
    ///
    /// Returns [`RegionAdmissionError::AlreadyActive`] if the code is
    /// already active, [`RegionAdmissionError::Reserved`] if the code was
    /// retired (forever), and [`RegionAdmissionError::CeilingExceeded`] if
    /// the catalog already holds [`REGION_CEILING`] active Regions.
    pub fn admit(&mut self, code: RegionCode) -> Result<(), RegionAdmissionError> {
        if self.active.contains(&code) {
            return Err(RegionAdmissionError::AlreadyActive(code));
        }
        if self.reserved.contains(&code) {
            return Err(RegionAdmissionError::Reserved(code));
        }
        if self.active.len() >= REGION_CEILING {
            return Err(RegionAdmissionError::CeilingExceeded {
                active: self.active.len(),
                ceiling: REGION_CEILING,
            });
        }
        self.active.push(code);
        Ok(())
    }

    /// Retires one active Region: the code leaves the active set and
    /// becomes reserved. A reserved code can never be admitted again, so
    /// it can never later name a different geography.
    ///
    /// # Errors
    ///
    /// Returns [`RegionAdmissionError::NotActive`] if the code is not an
    /// active Region of this catalog (it was never admitted or is already
    /// retired).
    pub fn retire(&mut self, code: RegionCode) -> Result<(), RegionAdmissionError> {
        let Some(position) = self.active.iter().position(|active| *active == code) else {
            return Err(RegionAdmissionError::NotActive(code));
        };
        self.active.remove(position);
        self.reserved.push(code);
        Ok(())
    }

    fn check_registration(&self, code: RegionCode) -> Result<RegionCode, RegionCodeError> {
        if self.active.contains(&code) {
            Ok(code)
        } else if self.reserved.contains(&code) {
            Err(RegionCodeError::Retired(code))
        } else {
            Err(RegionCodeError::Unregistered(code))
        }
    }
}

impl Default for RegionCatalog {
    fn default() -> Self {
        Self::production()
    }
}

#[cfg(test)]
mod tests {
    use super::{
        REGION_CEILING, REGION_CODE_BYTES, RegionAdmissionError, RegionCatalog, RegionCode,
        RegionCodeError,
    };
    use std::str::FromStr;

    /// Parses well-formed unregistered text and returns its `RegionCode`.
    fn unregistered(text: &str) -> RegionCode {
        RegionCode::from_text(text).expect("well-formed three-letter code")
    }

    #[test]
    fn the_production_catalog_is_exactly_the_three_initial_regions() {
        let catalog = RegionCatalog::production();
        assert_eq!(catalog.active(), &RegionCatalog::INITIAL_ACTIVE[..]);
        assert_eq!(catalog.active(), &[RegionCode::GRU, RegionCode::SFO, RegionCode::FRA]);
        assert!(catalog.reserved().is_empty());
        assert_eq!(RegionCatalog::default(), catalog);
        assert_eq!(catalog.active().len(), 3);
    }

    #[test]
    fn the_region_constants_round_trip_text_and_wire_bytes() {
        for code in [RegionCode::GRU, RegionCode::SFO, RegionCode::FRA] {
            let text = code.as_str();
            assert_eq!(text.len(), REGION_CODE_BYTES);
            assert_eq!(RegionCode::from_text(text), Ok(code));
            assert_eq!(RegionCode::from_wire_bytes(code.as_bytes()), Ok(code));
            assert_eq!(text, code.to_string());
        }
        assert_eq!(RegionCode::GRU.as_bytes(), *b"GRU");
        assert_eq!(RegionCode::SFO.as_bytes(), *b"SFO");
        assert_eq!(RegionCode::FRA.as_bytes(), *b"FRA");
        assert_eq!(RegionCode::GRU.as_str(), "GRU");
    }

    #[test]
    fn parse_accepts_each_registered_code_in_both_forms() {
        let catalog = RegionCatalog::production();
        for text in ["GRU", "SFO", "FRA"] {
            let code = catalog.parse(text).expect("registered code parses");
            assert_eq!(code.as_str(), text);
            let wire: [u8; REGION_CODE_BYTES] = text.as_bytes().try_into().expect("three bytes");
            assert_eq!(catalog.parse_wire_bytes(wire), Ok(code));
            assert_eq!(RegionCode::from_str(text), Ok(code));
        }
    }

    #[test]
    fn wrong_lengths_are_refused_including_padded_and_multibyte_blowup() {
        let catalog = RegionCatalog::production();
        // Empty, short, long, and whitespace-padded registered codes.
        assert_eq!(catalog.parse(""), Err(RegionCodeError::WrongLength(0)));
        assert_eq!(catalog.parse("G"), Err(RegionCodeError::WrongLength(1)));
        assert_eq!(catalog.parse("GR"), Err(RegionCodeError::WrongLength(2)));
        assert_eq!(catalog.parse("GRUU"), Err(RegionCodeError::WrongLength(4)));
        assert_eq!(catalog.parse("GRU "), Err(RegionCodeError::WrongLength(4)));
        assert_eq!(catalog.parse(" GRU"), Err(RegionCodeError::WrongLength(4)));
        assert_eq!(catalog.parse("GRU\n"), Err(RegionCodeError::WrongLength(4)));
        // Fullwidth characters are nine bytes: still a wrong length.
        assert_eq!(catalog.parse("ＧＲＵ"), Err(RegionCodeError::WrongLength(9)));
        // The shape constructor refuses the same class without a catalog.
        assert_eq!(RegionCode::from_text("GR"), Err(RegionCodeError::WrongLength(2)));
    }

    #[test]
    fn lowercase_text_is_refused_and_never_normalized() {
        let catalog = RegionCatalog::production();
        assert_eq!(
            catalog.parse("gru"),
            Err(RegionCodeError::NotUppercaseLetter { index: 0, byte: b'g' })
        );
        assert_eq!(
            catalog.parse("sfo"),
            Err(RegionCodeError::NotUppercaseLetter { index: 0, byte: b's' })
        );
        assert_eq!(
            catalog.parse("fra"),
            Err(RegionCodeError::NotUppercaseLetter { index: 0, byte: b'f' })
        );
        // Mixed case names the first lowercase byte, proving byte-exact
        // inspection rather than any case folding.
        assert_eq!(
            catalog.parse("Gru"),
            Err(RegionCodeError::NotUppercaseLetter { index: 1, byte: b'r' })
        );
        assert_eq!(
            catalog.parse("GRu"),
            Err(RegionCodeError::NotUppercaseLetter { index: 2, byte: b'u' })
        );
        // The refusal is typed: no normalization path exists.
        assert_ne!(catalog.parse("gru"), Ok(RegionCode::GRU));
        assert_eq!(
            RegionCode::from_text("gRu"),
            Err(RegionCodeError::NotUppercaseLetter { index: 0, byte: b'g' })
        );
    }

    #[test]
    fn ascii_bytes_outside_strict_uppercase_letters_are_refused() {
        let catalog = RegionCatalog::production();
        let cases: [(&str, usize, u8); 6] = [
            ("GR1", 2, b'1'),
            ("G-U", 1, b'-'),
            ("G U", 1, b' '),
            ("G\tU", 1, b'\t'),
            ("G\0U", 1, 0),
            ("G,R", 1, b','),
        ];
        for (text, index, byte) in cases {
            assert_eq!(
                catalog.parse(text),
                Err(RegionCodeError::NotUppercaseLetter { index, byte }),
                "refused {text:?}"
            );
        }
    }

    #[test]
    fn non_ascii_text_is_refused() {
        let catalog = RegionCatalog::production();
        // A three-byte CJK character is exactly the wire width but is not
        // ASCII text.
        assert_eq!(catalog.parse("京"), Err(RegionCodeError::NonAscii { index: 0 }));
        // A two-byte accented letter after one ASCII letter.
        assert_eq!(catalog.parse("GÉ"), Err(RegionCodeError::NonAscii { index: 1 }));
        // The wire constructor sees raw bytes, even invalid UTF-8 that
        // could never arrive as text.
        assert_eq!(
            RegionCode::from_wire_bytes([0xC3, 0x28, 0x47]),
            Err(RegionCodeError::NonAscii { index: 0 })
        );
    }

    #[test]
    fn unregistered_codes_are_refused() {
        let catalog = RegionCatalog::production();
        for text in ["JFK", "ABC", "ZZZ", "GRW", "XXX"] {
            let code = unregistered(text);
            assert_eq!(catalog.parse(text), Err(RegionCodeError::Unregistered(code)));
            assert_eq!(
                catalog.parse_wire_bytes(code.as_bytes()),
                Err(RegionCodeError::Unregistered(code))
            );
            assert!(!catalog.is_registered(&code));
        }
        // The layering is explicit: shape validation alone accepts a
        // well-formed code; only the catalog refuses it.
        assert!(RegionCode::from_text("JFK").is_ok());
    }

    #[test]
    fn sjc_is_refused_and_is_never_an_alias_of_sfo() {
        // ADR 0033: `SFO` is the stable code even though provider material
        // labels the same location with a different airport code. The
        // engine keeps no alias table, so that code is just unregistered
        // text and must stay refused.
        let catalog = RegionCatalog::production();
        let sjc = unregistered("SJC");
        assert_eq!(catalog.parse("SJC"), Err(RegionCodeError::Unregistered(sjc)));
        assert_eq!(catalog.parse_wire_bytes(*b"SJC"), Err(RegionCodeError::Unregistered(sjc)));
        // The refusal never resolves to SFO, and SFO itself is unaffected.
        assert!(!catalog.is_registered(&sjc));
        assert!(catalog.is_registered(&RegionCode::SFO));
        assert_eq!(catalog.parse("SFO"), Ok(RegionCode::SFO));
    }

    #[test]
    fn a_retired_code_stays_reserved_and_can_never_be_readmitted() {
        let mut catalog = RegionCatalog::production();
        let mia = unregistered("MIA");
        catalog.admit(mia).expect("admits a fresh code");
        assert!(catalog.is_registered(&mia));

        catalog.retire(mia).expect("retires an active Region");
        assert!(!catalog.is_registered(&mia), "a retired code is not active");
        assert!(catalog.is_reserved(&mia));
        assert_eq!(catalog.reserved(), &[mia]);

        // Parsing names the retirement, not plain unregistration.
        assert_eq!(catalog.parse("MIA"), Err(RegionCodeError::Retired(mia)));
        assert_eq!(catalog.parse_wire_bytes(mia.as_bytes()), Err(RegionCodeError::Retired(mia)));

        // The forever invariant: readmission is refused while the code
        // stays reserved, so it can never name a different geography.
        assert_eq!(catalog.admit(mia), Err(RegionAdmissionError::Reserved(mia)));
        assert_eq!(catalog.retire(mia), Err(RegionAdmissionError::NotActive(mia)));

        // The initial production Regions are untouched.
        assert_eq!(catalog.active(), &RegionCatalog::INITIAL_ACTIVE[..]);
    }

    #[test]
    fn admission_refuses_duplicates_and_never_admitted_codes_cannot_retire() {
        let mut catalog = RegionCatalog::production();
        assert_eq!(
            catalog.admit(RegionCode::GRU),
            Err(RegionAdmissionError::AlreadyActive(RegionCode::GRU))
        );
        let jfk = unregistered("JFK");
        catalog.admit(jfk).expect("admits a fresh code");
        assert_eq!(catalog.admit(jfk), Err(RegionAdmissionError::AlreadyActive(jfk)));
        // Retiring requires an active Region: a well-formed code that was
        // never admitted refuses.
        let lax = unregistered("LAX");
        assert_eq!(catalog.retire(lax), Err(RegionAdmissionError::NotActive(lax)));
        // A reserved code is not active, so retiring it again is NotActive.
        catalog.retire(jfk).expect("retires");
        assert_eq!(catalog.retire(jfk), Err(RegionAdmissionError::NotActive(jfk)));
    }

    #[test]
    fn the_catalog_enforces_the_twenty_region_ceiling() {
        assert_eq!(REGION_CEILING, 20, "the charter's architecture ceiling");
        let mut catalog = RegionCatalog::production();
        // Seventeen fresh codes reach exactly the ceiling of twenty
        // active Regions.
        for letter in b'A'..=b'Q' {
            let text = [letter, letter, letter];
            let code = RegionCode::from_wire_bytes(text).expect("strict uppercase letters");
            catalog.admit(code).expect("below the ceiling");
        }
        assert_eq!(catalog.active().len(), REGION_CEILING);
        let next = unregistered("RRR");
        assert_eq!(
            catalog.admit(next),
            Err(RegionAdmissionError::CeilingExceeded { active: REGION_CEILING, ceiling: 20 })
        );

        // Reserved codes are not Regions: retiring one frees a seat that a
        // different code can take, while the retired code itself stays
        // refused.
        let aaa = unregistered("AAA");
        catalog.retire(aaa).expect("retires");
        assert_eq!(catalog.active().len(), REGION_CEILING - 1);
        catalog.admit(next).expect("a freed seat is below the ceiling again");
        assert_eq!(
            catalog.admit(aaa),
            Err(RegionAdmissionError::Reserved(aaa)),
            "retirement does not free the retired code itself"
        );
    }
}
