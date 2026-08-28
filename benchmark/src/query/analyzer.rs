//! The analyzer and term-vector foundation (GAP-Q13, TerraBaseV2#67).
//!
//! V1's relevance features — search, highlighting, suggestions,
//! more-like-this — all stand on one primitive: an analyzer turns one field
//! value into ordered terms, and the index records what the analyzer
//! produced. V1's engine is Corax, which runs a tokenizer plus an ordered
//! transformer pipeline, keeps the per-entry term order as its positional
//! "term vector", and exposes the per-field term-vector modes as a client
//! contract.
//!
//! This module is that foundation in typed shape, and it is deliberately
//! **not** a search engine. It owns exactly three things: the closed set of
//! analyzer configurations a field may name ([`AnalyzerKind`], resolved
//! from the analyzer names the index-definition language already carries),
//! the versioned term-vector contract ([`TermVector`]) that records what an
//! analysis produced, and the sandbox rules that make analysis a bounded,
//! deterministic, code-free operation. Nothing here scores, matches,
//! ranks, or serves a query; no index reads or writes through it yet.
//!
//! ## The sandbox rules (tested in this module)
//!
//! 1. **No arbitrary code.** An analyzer is a typed value. There is no
//!    variant, field, or name anywhere in this contract that can carry
//!    source code, and therefore nothing to compile, load, or execute. V1
//!    compiles C# analyzer source at deployment; V2 rejects that entire
//!    execution model — the charter's no-arbitrary-code rule
//!    (PD-2026-08-26-08 D2) applied to the analysis surface, the same rule
//!    that replaced RQL's JavaScript projections with typed TQL
//!    expressions.
//! 2. **Bounded tokenization.** Input size, term size, and token count are
//!    each capped by a named constant, and every cap refuses with a typed
//!    error before unbounded state exists ([`AnalyzerError`]). V1's only
//!    comparable guard is an overflow guard of half an `int`'s terms per
//!    field; V2's bounds are engine bounds, not overflow guards.
//! 3. **No recursion.** Tokenization is one linear walk over the value.
//!    There is no recursive function on this path and no input that can
//!    deepen one.
//! 4. **Unknown fails closed.** An analyzer name outside the closed set, a
//!    term-vector mode outside the contract, or a contract version other
//!    than the one this engine speaks is refused — never guessed, never
//!    substituted with equality. V1 refuses an unknown analyzer name too;
//!    V2 refuses it with a typed error.
//!
//! The full decision record — the engine-surface decision this packet owes
//! (PROPOSED for parent ratification), the alternatives with their
//! evidence, the V1 file-and-line anchors, the Q17 disposition, and the
//! WS-PARITY proposals for the six relevance rows — is
//! `docs/query/analyzer-foundation.md`. Every V1 comparison in the
//! doc comments below states its anchor in prose; the pinned file and line
//! for each is in that record.

use std::collections::BTreeMap;

use super::spec::validate_field_name;

/// The identity prefix of the analyzer contract version.
///
/// Mirrors the TQL grammar version and the index-definition language
/// version (`tql/`, `idx/`): one version identity per artifact, carried by
/// every term vector this contract produces and accepted from nowhere else.
pub const ANALYZER_LANGUAGE_PREFIX: &str = "analyzer/";

/// The analyzer and term-vector contract version this engine speaks.
///
/// Every [`TermVector`] carries it, and construction from an external
/// version refuses anything else before any other validation runs.
/// Additive contract changes bump the minor; breaking changes bump the
/// major.
pub const ANALYZER_LANGUAGE_VERSION: &str = "analyzer/1.0";

/// The byte bound of one analyzer name, shared with the seam's identifier
/// rule (`MAX_NAME_BYTES` of the index-definition language).
pub const MAX_ANALYZER_NAME_BYTES: usize = 255;

/// The maximum number of token filters one composite analyzer may compose.
///
/// An initial engine bound: V1 composes an unbounded transformer array, so
/// the typed language keeps the pipeline bounded for the same reason it
/// keeps every other list bounded — a configuration is engine state.
pub const MAX_TOKEN_FILTERS: usize = 4;

/// The maximum size in bytes of one field value submitted to analysis.
///
/// An initial engine bound, tunable with measured evidence. V1 sizes its
/// analyzer buffers from the input (4 KiB default) and grows them as
/// needed; V2 refuses the input instead of growing without a ceiling.
pub const MAX_ANALYZED_VALUE_BYTES: usize = 1024 * 1024;

/// The maximum size in bytes of one analyzed term.
///
/// The bound a single token contributes to engine state. V1 has no
/// term-length bound — a keyword token is the whole value — so V2's
/// tighter bound is a recorded accepted difference.
pub const MAX_TERM_BYTES: usize = 4096;

/// The maximum number of token occurrences one field value may produce.
///
/// This is the term-count cap of the sandbox: it bounds positions, offsets,
/// and the total frequency mass of one term vector, and tokenization checks
/// it as it emits, so a pathological value refuses instead of allocating
/// toward its length.
pub const MAX_TOKENS_PER_FIELD: usize = 8192;

/// The typed, content-safe error vocabulary of the analyzer foundation.
///
/// Every variant names the violated contract rule. No variant carries a
/// document value, a field value, or an analyzer payload, so errors stay
/// safe to observe, record, and cross an adapter.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AnalyzerError {
    /// The contract version is not a well-formed `analyzer/MAJOR.MINOR`
    /// identity, or is not the version this engine speaks.
    UnknownVersion,
    /// The analyzer name does not name a member of the closed analyzer set.
    /// The name is not echoed: the failure is the fact, not the input.
    UnknownAnalyzer,
    /// The analyzer name is empty, oversized, or carries control characters
    /// or whitespace padding.
    InvalidAnalyzerName,
    /// A composite analyzer composes more filters than the bound.
    TooManyFilters,
    /// A composite analyzer composes one filter twice; the pipeline order
    /// is part of its identity, so a duplicate is refused, not collapsed.
    DuplicateFilter,
    /// The field value submitted to analysis exceeds
    /// [`MAX_ANALYZED_VALUE_BYTES`].
    InputTooLarge,
    /// One analyzed term exceeds [`MAX_TERM_BYTES`].
    TermTooLarge,
    /// The field value would produce more token occurrences than
    /// [`MAX_TOKENS_PER_FIELD`].
    TokenBoundExceeded,
    /// A term-vector value violates its construction contract: a `No` mode
    /// carried as a vector, a frequency of zero, a position or offset list
    /// that disagrees with the frequency, unordered or out-of-bound
    /// positions, overlapping or out-of-bound offsets, an entry shape that
    /// disagrees with the vector's mode, or an empty or oversized term.
    InvalidTermVector,
}

/// The tokenizer kinds of the closed analyzer set.
///
/// The three kinds are the tokenization behaviors V1's defaults actually
/// instantiate: one whole-value term, whitespace-delimited terms, and word
/// terms. See [`AnalyzerKind::Standard`] for what "word" means here and
/// what is deliberately not claimed for it.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TokenizerKind {
    /// One term: the whole value, unsplit.
    Keyword,
    /// One term per maximal run of non-whitespace characters.
    Whitespace,
    /// One term per maximal run of Unicode alphanumeric characters.
    Standard,
}

impl TokenizerKind {
    /// Returns the canonical identity token of the kind.
    #[must_use]
    pub const fn identity(self) -> &'static str {
        match self {
            Self::Keyword => "keyword",
            Self::Standard => "standard",
            Self::Whitespace => "whitespace",
        }
    }
}

/// The token filters of the closed analyzer set.
///
/// A filter maps term text; it never splits, reorders, or drops tokens, so
/// token positions and offsets survive every filter unchanged.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TokenFilterKind {
    /// Lowercases the term text with the Unicode default case mapping.
    ///
    /// V1's lower-case transformer is the anchor. Offsets stay offsets
    /// into the original value even when lowercasing changes the term's
    /// byte length — a V2 declaration: V1's token offsets index the
    /// transformed buffer.
    LowerCase,
}

impl TokenFilterKind {
    /// Returns the canonical identity token of the kind.
    #[must_use]
    pub const fn identity(self) -> &'static str {
        match self {
            Self::LowerCase => "lowercase",
        }
    }

    /// Applies the filter to one term's text.
    fn apply(self, term: &str) -> String {
        match self {
            Self::LowerCase => term.to_lowercase(),
        }
    }
}

/// One analyzer configuration: the typed, code-free unit the analysis
/// surface is built from.
///
/// Three named configurations are V1's engine defaults, carried under V1's
/// own names so an index definition authored against V1 vocabulary resolves
/// without translation:
///
/// | Variant | V1 name at the pin | V1 role |
/// | --- | --- | --- |
/// | [`Self::Keyword`] | `KeywordAnalyzer` | the exact default |
/// | [`Self::LowercaseKeyword`] | `LowerCaseKeywordAnalyzer` | the general default |
/// | [`Self::Standard`] | `TerraBaseStandardAnalyzer` | the search default |
///
/// V1's Corax builds the exact default as its keyword tokenizer plus the
/// exact transformer, and the general default as the same tokenizer plus
/// its lower-case transformer; the record carries the pinned file and line
/// for each.
///
/// [`Self::Composite`] is the declarative escape hatch for configurations
/// the three defaults do not cover — a closed tokenizer plus an ordered,
/// bounded filter list, never code. That is the accepted replacement for
/// V1's compiled custom analyzers, not a compatibility surface for them.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum AnalyzerKind {
    /// One exact term per value: V1's `KeywordAnalyzer`.
    Keyword,
    /// One lowercased term per value: V1's `LowerCaseKeywordAnalyzer`.
    LowercaseKeyword,
    /// Word terms, lowercased: V1's `TerraBaseStandardAnalyzer`.
    Standard,
    /// A declarative composition of one tokenizer and an ordered filter
    /// list.
    Composite {
        /// The tokenizer that splits the value into tokens.
        tokenizer: TokenizerKind,
        /// The filters applied to each token's term text, in order.
        filters: Vec<TokenFilterKind>,
    },
}

impl AnalyzerKind {
    /// The canonical name of V1's exact default analyzer.
    pub const KEYWORD_NAME: &'static str = "KeywordAnalyzer";

    /// The canonical name of V1's general default analyzer.
    pub const LOWERCASE_KEYWORD_NAME: &'static str = "LowerCaseKeywordAnalyzer";

    /// The canonical name of V1's search default analyzer.
    pub const STANDARD_NAME: &'static str = "TerraBaseStandardAnalyzer";

    /// Resolves one analyzer name against the closed set.
    ///
    /// The three V1 default names resolve; every other name — including
    /// V1's arbitrary assembly-qualified type spellings and any compiled
    /// custom analyzer name — fails closed with
    /// [`AnalyzerError::UnknownAnalyzer`]. This is the seam where the
    /// index-definition language's
    /// [`AnalyzerName`](crate::index::definitions::AnalyzerName) gains a
    /// meaning: a definition's analyzer names are validated here before
    /// any analysis runs.
    ///
    /// # Errors
    ///
    /// Returns [`AnalyzerError::InvalidAnalyzerName`] for an empty,
    /// oversized, control-carrying, or padded name, and
    /// [`AnalyzerError::UnknownAnalyzer`] for a well-formed name outside
    /// the closed set.
    pub fn resolve(name: &str) -> Result<Self, AnalyzerError> {
        validate_analyzer_name(name)?;
        match name {
            Self::KEYWORD_NAME => Ok(Self::Keyword),
            Self::LOWERCASE_KEYWORD_NAME => Ok(Self::LowercaseKeyword),
            Self::STANDARD_NAME => Ok(Self::Standard),
            _ => Err(AnalyzerError::UnknownAnalyzer),
        }
    }

    /// Composes a declarative analyzer from one tokenizer and an ordered
    /// filter list.
    ///
    /// # Errors
    ///
    /// Returns [`AnalyzerError::TooManyFilters`] for a list longer than
    /// [`MAX_TOKEN_FILTERS`] and [`AnalyzerError::DuplicateFilter`] for a
    /// repeated filter.
    pub fn compose(
        tokenizer: TokenizerKind,
        filters: Vec<TokenFilterKind>,
    ) -> Result<Self, AnalyzerError> {
        if filters.len() > MAX_TOKEN_FILTERS {
            return Err(AnalyzerError::TooManyFilters);
        }
        for (index, filter) in filters.iter().enumerate() {
            if filters[..index].contains(filter) {
                return Err(AnalyzerError::DuplicateFilter);
            }
        }
        Ok(Self::Composite { tokenizer, filters })
    }

    /// Returns the tokenizer behavior of the configuration.
    #[must_use]
    pub const fn tokenizer(&self) -> TokenizerKind {
        match self {
            Self::Keyword | Self::LowercaseKeyword => TokenizerKind::Keyword,
            Self::Standard => TokenizerKind::Standard,
            Self::Composite { tokenizer, .. } => *tokenizer,
        }
    }

    /// Returns the filters of the configuration, in application order.
    #[must_use]
    pub fn filters(&self) -> Vec<TokenFilterKind> {
        match self {
            Self::Keyword => Vec::new(),
            Self::LowercaseKeyword | Self::Standard => vec![TokenFilterKind::LowerCase],
            Self::Composite { filters, .. } => filters.clone(),
        }
    }

    /// Returns the canonical identity of the configuration.
    ///
    /// The three named configurations carry their V1 names verbatim, so an
    /// identity is stable across engine state, drift comparison, and wire
    /// surfaces. A composite carries its closed composition grammar.
    #[must_use]
    pub fn identity(&self) -> String {
        match self {
            Self::Keyword => Self::KEYWORD_NAME.to_owned(),
            Self::LowercaseKeyword => Self::LOWERCASE_KEYWORD_NAME.to_owned(),
            Self::Standard => Self::STANDARD_NAME.to_owned(),
            Self::Composite { tokenizer, filters } => {
                let mut identity = format!("analyzer:{}", tokenizer.identity());
                for filter in filters {
                    identity.push('+');
                    identity.push_str(filter.identity());
                }
                identity
            }
        }
    }
}

/// Validates one analyzer name against the seam's identifier rule.
fn validate_analyzer_name(name: &str) -> Result<(), AnalyzerError> {
    if name.is_empty() {
        return Err(AnalyzerError::InvalidAnalyzerName);
    }
    if name.len() > MAX_ANALYZER_NAME_BYTES
        || name.chars().any(char::is_control)
        || name != name.trim()
    {
        return Err(AnalyzerError::InvalidAnalyzerName);
    }
    Ok(())
}

/// One analyzer: a validated configuration plus the analysis operation.
///
/// The configuration is validated once at construction and never changes;
/// [`Analyzer::analyze`](Analyzer::analyze) is a pure function of the
/// configuration, the field name, the value, and the requested mode. It
/// holds no buffer, no cache, no engine handle, and no lock.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Analyzer {
    kind: AnalyzerKind,
}

impl Analyzer {
    /// Creates an analyzer from one validated configuration.
    ///
    /// # Errors
    ///
    /// Returns the composition error of a [`AnalyzerKind::Composite`]
    /// built outside [`AnalyzerKind::compose`]; the three named kinds are
    /// always valid.
    pub fn try_new(kind: AnalyzerKind) -> Result<Self, AnalyzerError> {
        if let AnalyzerKind::Composite { filters, .. } = &kind {
            if filters.len() > MAX_TOKEN_FILTERS {
                return Err(AnalyzerError::TooManyFilters);
            }
            for (index, filter) in filters.iter().enumerate() {
                if filters[..index].contains(filter) {
                    return Err(AnalyzerError::DuplicateFilter);
                }
            }
        }
        Ok(Self { kind })
    }

    /// Resolves one analyzer name into a ready analyzer.
    ///
    /// # Errors
    ///
    /// Returns the resolution error of [`AnalyzerKind::resolve`].
    pub fn resolve(name: &str) -> Result<Self, AnalyzerError> {
        Ok(Self { kind: AnalyzerKind::resolve(name)? })
    }

    /// Returns the analyzer's configuration.
    #[must_use]
    pub const fn kind(&self) -> &AnalyzerKind {
        &self.kind
    }

    /// Returns the analyzer's canonical identity.
    #[must_use]
    pub fn identity(&self) -> String {
        self.kind.identity()
    }

    /// Analyzes one field value into a term vector of the requested mode.
    ///
    /// The operation is bounded, deterministic, and code-free: the input
    /// and every term are capped, the token count is capped as tokens are
    /// emitted, the walk is linear with no recursion, and the analyzer's
    /// configuration is the only input besides the value itself. Offsets
    /// are byte offsets into `value`; positions are token indices in
    /// emission order — the per-entry term order V1's engine keeps for
    /// phrase matching.
    ///
    /// An empty value produces an empty term vector: V2's term contract
    /// requires non-empty terms, where V1's keyword tokenizer emits one
    /// zero-length token — a recorded accepted difference.
    ///
    /// # Errors
    ///
    /// Returns [`AnalyzerError::InputTooLarge`] when the value exceeds
    /// [`MAX_ANALYZED_VALUE_BYTES`], [`AnalyzerError::TermTooLarge`] when
    /// a filtered term exceeds [`MAX_TERM_BYTES`],
    /// [`AnalyzerError::TokenBoundExceeded`] when the value would produce
    /// more than [`MAX_TOKENS_PER_FIELD`] tokens, and
    /// [`AnalyzerError::InvalidTermVector`] when the field name is invalid
    /// or the mode is [`TermVectorMode::No`].
    pub fn analyze(
        &self,
        field: &str,
        value: &str,
        mode: TermVectorMode,
    ) -> Result<TermVector, AnalyzerError> {
        validate_field_name(field).map_err(|_| AnalyzerError::InvalidTermVector)?;
        if mode == TermVectorMode::No {
            return Err(AnalyzerError::InvalidTermVector);
        }
        if value.len() > MAX_ANALYZED_VALUE_BYTES {
            return Err(AnalyzerError::InputTooLarge);
        }

        // The filter pipeline is borrowed, never rebuilt per call: the
        // named kinds resolve to static slices.
        let filters: &[TokenFilterKind] = match &self.kind {
            AnalyzerKind::Keyword => &[],
            AnalyzerKind::LowercaseKeyword | AnalyzerKind::Standard => {
                &[TokenFilterKind::LowerCase]
            }
            AnalyzerKind::Composite { filters, .. } => filters,
        };

        let mut tokens = Vec::new();
        for (start, end) in tokenize(self.kind.tokenizer(), value) {
            if tokens.len() >= MAX_TOKENS_PER_FIELD {
                return Err(AnalyzerError::TokenBoundExceeded);
            }
            let raw = &value[start..end];
            let mut term = raw.to_owned();
            for filter in filters {
                term = filter.apply(&term);
            }
            if term.len() > MAX_TERM_BYTES {
                return Err(AnalyzerError::TermTooLarge);
            }
            if term.is_empty() {
                continue;
            }
            let position = u32::try_from(tokens.len()).unwrap_or(u32::MAX);
            tokens.push((
                term,
                position,
                TermOffset {
                    start: u32::try_from(start).unwrap_or(u32::MAX),
                    end: u32::try_from(end).unwrap_or(u32::MAX),
                },
            ));
        }

        // Aggregation over a BTreeMap keeps one entry per distinct term in
        // byte order, so the same value under the same analyzer is one
        // deterministic vector.
        let mut entries: BTreeMap<String, (u32, Vec<u32>, Vec<TermOffset>)> = BTreeMap::new();
        for (term, position, offset) in tokens {
            let entry = entries.entry(term).or_insert_with(|| (0, Vec::new(), Vec::new()));
            entry.0 += 1;
            if mode.has_positions() {
                entry.1.push(position);
            }
            if mode.has_offsets() {
                entry.2.push(offset);
            }
        }

        let entries = entries
            .into_iter()
            .map(|(term, (frequency, positions, offsets))| {
                TermVectorEntry::try_new(term, frequency, positions, offsets)
            })
            .collect::<Result<Vec<_>, _>>()?;
        TermVector::try_new(field, mode, entries)
    }
}

/// Splits one value into token byte ranges under one tokenizer kind.
///
/// One linear walk, no recursion, and no allocation beyond the returned
/// ranges: the bound check belongs to the caller, which enforces it as it
/// consumes the ranges.
fn tokenize(kind: TokenizerKind, value: &str) -> Vec<(usize, usize)> {
    let mut tokens = Vec::new();
    if value.is_empty() {
        return tokens;
    }
    let keeps: Option<fn(char) -> bool> = match kind {
        TokenizerKind::Keyword => None,
        TokenizerKind::Whitespace => Some(|c: char| !c.is_whitespace()),
        TokenizerKind::Standard => Some(|c: char| c.is_alphanumeric()),
    };
    let Some(keeps) = keeps else {
        // Keyword: the whole value is one term.
        tokens.push((0, value.len()));
        return tokens;
    };

    let mut start: Option<usize> = None;
    for (index, character) in value.char_indices() {
        if keeps(character) {
            if start.is_none() {
                start = Some(index);
            }
        } else if let Some(begin) = start.take() {
            tokens.push((begin, index));
        }
    }
    if let Some(begin) = start {
        tokens.push((begin, value.len()));
    }
    tokens
}

/// The term-vector mode one field carries.
///
/// The closed mode set is V1's client contract:
/// no vector, frequencies only, or frequencies with positions and/or
/// offsets. `Frequencies` is V1's `Yes` under the name that says what it
/// carries; the other four carry V1's names verbatim, so the wire tokens
/// ([`TermVectorMode::wire_name`]) round-trip V1's enum member names.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TermVectorMode {
    /// No term vector is stored for the field.
    ///
    /// This is a field option, not a vector shape: it cannot be carried by
    /// a [`TermVector`], and construction refuses it.
    No,
    /// The field's distinct terms and their occurrence counts.
    Frequencies,
    /// Frequencies plus each occurrence's token position.
    WithPositions,
    /// Frequencies plus each occurrence's byte offsets.
    WithOffsets,
    /// Frequencies plus both positions and byte offsets.
    ///
    /// V1 requires exactly this shape on a highlighted field: its
    /// highlighting indexing mode is search analysis with positions and
    /// offsets tracked.
    WithPositionsAndOffsets,
}

impl TermVectorMode {
    /// Parses one wire token into a mode.
    ///
    /// # Errors
    ///
    /// Returns [`AnalyzerError::UnknownAnalyzer`] for any token outside
    /// the five contract names — unknown modes fail closed rather than
    /// degrading to a weaker vector.
    pub fn parse(token: &str) -> Result<Self, AnalyzerError> {
        match token {
            "No" => Ok(Self::No),
            "Yes" | "Frequencies" => Ok(Self::Frequencies),
            "WithPositions" => Ok(Self::WithPositions),
            "WithOffsets" => Ok(Self::WithOffsets),
            "WithPositionsAndOffsets" => Ok(Self::WithPositionsAndOffsets),
            _ => Err(AnalyzerError::UnknownAnalyzer),
        }
    }

    /// Returns the wire token of the mode: V1's enum member name.
    #[must_use]
    pub const fn wire_name(self) -> &'static str {
        match self {
            Self::No => "No",
            // V1's member name for the frequencies-only mode.
            Self::Frequencies => "Yes",
            Self::WithPositions => "WithPositions",
            Self::WithOffsets => "WithOffsets",
            Self::WithPositionsAndOffsets => "WithPositionsAndOffsets",
        }
    }

    /// Whether the mode records token positions.
    #[must_use]
    pub const fn has_positions(self) -> bool {
        matches!(self, Self::WithPositions | Self::WithPositionsAndOffsets)
    }

    /// Whether the mode records byte offsets.
    #[must_use]
    pub const fn has_offsets(self) -> bool {
        matches!(self, Self::WithOffsets | Self::WithPositionsAndOffsets)
    }

    /// Whether the mode records anything at all.
    #[must_use]
    pub const fn is_stored(self) -> bool {
        matches!(
            self,
            Self::Frequencies
                | Self::WithPositions
                | Self::WithOffsets
                | Self::WithPositionsAndOffsets
        )
    }
}

/// The byte range of one token occurrence in the analyzed value.
///
/// Offsets are UTF-8 byte offsets into the original field value, not
/// character indices and not offsets into the filtered term text — a V2
/// declaration (V1's token offsets index the analyzer's transformed
/// buffer). The range is half-open: `start < end <= end of value`.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct TermOffset {
    start: u32,
    end: u32,
}

impl TermOffset {
    /// Creates one validated offset range.
    ///
    /// # Errors
    ///
    /// Returns [`AnalyzerError::InvalidTermVector`] when `start >= end` or
    /// `end` exceeds [`MAX_ANALYZED_VALUE_BYTES`].
    pub const fn try_new(start: u32, end: u32) -> Result<Self, AnalyzerError> {
        if start >= end || end as usize > MAX_ANALYZED_VALUE_BYTES {
            return Err(AnalyzerError::InvalidTermVector);
        }
        Ok(Self { start, end })
    }

    /// Returns the inclusive start byte offset.
    #[must_use]
    pub const fn start(self) -> u32 {
        self.start
    }

    /// Returns the exclusive end byte offset.
    #[must_use]
    pub const fn end(self) -> u32 {
        self.end
    }
}

/// The term-vector record of one distinct term in one field value.
///
/// The entry carries the term text, the number of its occurrences, and —
/// when the vector's mode asks for them — one position and one offset per
/// occurrence, in occurrence order. The lists are exactly as long as the
/// frequency when they are present, and empty when the mode does not carry
/// them; an entry never mixes the two shapes.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TermVectorEntry {
    term: String,
    frequency: u32,
    positions: Vec<u32>,
    offsets: Vec<TermOffset>,
}

impl TermVectorEntry {
    /// Creates one validated entry.
    ///
    /// # Errors
    ///
    /// Returns [`AnalyzerError::InvalidTermVector`] when the term is empty
    /// or longer than [`MAX_TERM_BYTES`], the frequency is zero or exceeds
    /// [`MAX_TOKENS_PER_FIELD`], the position or offset lists disagree
    /// with the frequency, positions are not strictly increasing or reach
    /// outside the token bound, or offsets are unordered, overlapping, or
    /// carry an invalid range.
    pub fn try_new(
        term: String,
        frequency: u32,
        positions: Vec<u32>,
        offsets: Vec<TermOffset>,
    ) -> Result<Self, AnalyzerError> {
        if term.is_empty() || term.len() > MAX_TERM_BYTES {
            return Err(AnalyzerError::InvalidTermVector);
        }
        if frequency == 0 || frequency as usize > MAX_TOKENS_PER_FIELD {
            return Err(AnalyzerError::InvalidTermVector);
        }
        if !positions.is_empty() {
            if positions.len() != frequency as usize {
                return Err(AnalyzerError::InvalidTermVector);
            }
            validate_positions(&positions)?;
        }
        if !offsets.is_empty() {
            if offsets.len() != frequency as usize {
                return Err(AnalyzerError::InvalidTermVector);
            }
            validate_offsets(&offsets)?;
        }
        Ok(Self { term, frequency, positions, offsets })
    }

    /// Returns the analyzed term text.
    #[must_use]
    pub fn term(&self) -> &str {
        &self.term
    }

    /// Returns how often the term occurs in the field value.
    #[must_use]
    pub const fn frequency(&self) -> u32 {
        self.frequency
    }

    /// Returns the occurrence positions, when the vector's mode carries
    /// them.
    #[must_use]
    pub fn positions(&self) -> &[u32] {
        &self.positions
    }

    /// Returns the occurrence offsets, when the vector's mode carries them.
    #[must_use]
    pub fn offsets(&self) -> &[TermOffset] {
        &self.offsets
    }
}

/// Validates that positions are strictly increasing token indices inside
/// the token bound.
fn validate_positions(positions: &[u32]) -> Result<(), AnalyzerError> {
    let mut previous: Option<u32> = None;
    for position in positions {
        if *position as usize >= MAX_TOKENS_PER_FIELD {
            return Err(AnalyzerError::InvalidTermVector);
        }
        if previous.is_some_and(|seen| seen >= *position) {
            return Err(AnalyzerError::InvalidTermVector);
        }
        previous = Some(*position);
    }
    Ok(())
}

/// Validates that offsets are ordered, non-overlapping, in-range ranges —
/// the shape one linear tokenizer pass can produce.
fn validate_offsets(offsets: &[TermOffset]) -> Result<(), AnalyzerError> {
    let mut reach: Option<u32> = None;
    for offset in offsets {
        TermOffset::try_new(offset.start, offset.end)?;
        if reach.is_some_and(|end| end > offset.start) {
            return Err(AnalyzerError::InvalidTermVector);
        }
        reach = Some(offset.end);
    }
    Ok(())
}

/// The versioned term vector of one field value under one analyzer.
///
/// A term vector is the record of one analysis: the field it belongs to,
/// the mode it was built in, and one entry per distinct term in term byte
/// order. It is pure output — a value that can be stored, compared,
/// versioned, and replayed — and it is the contract the relevance rows
/// (highlighting, more-like-this, suggestions) consume: they need
/// positions and offsets, and this is where those shapes are typed.
///
/// Construction is total: every invariant (mode agreement, entry order,
/// frequency mass, bounds) is checked before a vector exists, so a
/// [`TermVector`] can never carry a shape its mode does not describe.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TermVector {
    contract_version: String,
    field: String,
    mode: TermVectorMode,
    entries: Vec<TermVectorEntry>,
}

impl TermVector {
    /// Creates one term vector in the contract version this engine speaks.
    ///
    /// # Errors
    ///
    /// Returns the construction error of
    /// [`TermVector::try_from_parts`] under
    /// [`ANALYZER_LANGUAGE_VERSION`].
    pub fn try_new(
        field: &str,
        mode: TermVectorMode,
        entries: Vec<TermVectorEntry>,
    ) -> Result<Self, AnalyzerError> {
        Self::try_from_parts(ANALYZER_LANGUAGE_VERSION, field, mode, entries)
    }

    /// Creates one term vector from an explicit contract version, failing
    /// closed on any version this engine does not speak before any other
    /// validation runs.
    ///
    /// This is the inbound seam for a stored or wire-carried term vector:
    /// a vector authored against any other `analyzer/MAJOR.MINOR`
    /// identity — older, newer, or malformed — is refused whole, exactly
    /// as an unknown TQL grammar version or index-definition language
    /// version is.
    ///
    /// # Errors
    ///
    /// Returns [`AnalyzerError::UnknownVersion`] when the version is not
    /// [`ANALYZER_LANGUAGE_VERSION`], and
    /// [`AnalyzerError::InvalidTermVector`] when the field name is
    /// invalid, the mode is [`TermVectorMode::No`], the entries are out of
    /// term order or duplicated, the entries exceed
    /// [`MAX_TOKENS_PER_FIELD`], an entry's shape disagrees with the mode,
    /// or the total frequency mass exceeds [`MAX_TOKENS_PER_FIELD`].
    pub fn try_from_parts(
        contract_version: &str,
        field: &str,
        mode: TermVectorMode,
        entries: Vec<TermVectorEntry>,
    ) -> Result<Self, AnalyzerError> {
        if contract_version != ANALYZER_LANGUAGE_VERSION {
            return Err(AnalyzerError::UnknownVersion);
        }
        validate_field_name(field).map_err(|_| AnalyzerError::InvalidTermVector)?;
        if !mode.is_stored() {
            return Err(AnalyzerError::InvalidTermVector);
        }
        if entries.len() > MAX_TOKENS_PER_FIELD {
            return Err(AnalyzerError::InvalidTermVector);
        }
        let mut total: usize = 0;
        for (index, entry) in entries.iter().enumerate() {
            if index > 0 && entries[index - 1].term.as_str() >= entry.term.as_str() {
                return Err(AnalyzerError::InvalidTermVector);
            }
            if entry.positions.is_empty() == mode.has_positions() {
                return Err(AnalyzerError::InvalidTermVector);
            }
            if entry.offsets.is_empty() == mode.has_offsets() {
                return Err(AnalyzerError::InvalidTermVector);
            }
            total = total.saturating_add(entry.frequency as usize);
        }
        if total > MAX_TOKENS_PER_FIELD {
            return Err(AnalyzerError::InvalidTermVector);
        }
        Ok(Self {
            contract_version: contract_version.to_owned(),
            field: field.to_owned(),
            mode,
            entries,
        })
    }

    /// Returns the contract version this vector was built in.
    #[must_use]
    pub fn contract_version(&self) -> &str {
        &self.contract_version
    }

    /// Returns the field the vector belongs to.
    #[must_use]
    pub fn field(&self) -> &str {
        &self.field
    }

    /// Returns the mode the vector was built in.
    #[must_use]
    pub const fn mode(&self) -> TermVectorMode {
        self.mode
    }

    /// Returns the entries, in term byte order.
    #[must_use]
    pub fn entries(&self) -> &[TermVectorEntry] {
        &self.entries
    }

    /// Returns the total number of token occurrences the vector records.
    #[must_use]
    pub fn total_tokens(&self) -> u32 {
        self.entries.iter().map(TermVectorEntry::frequency).sum()
    }

    /// Returns the frequency of one term, or `None` when the term is not
    /// in the vector.
    #[must_use]
    pub fn frequency_of(&self, term: &str) -> Option<u32> {
        self.entries.iter().find(|entry| entry.term() == term).map(TermVectorEntry::frequency)
    }
}

#[cfg(test)]
mod tests {
    use super::super::spec::MAX_FIELD_BYTES;
    use super::*;

    /// A value under every bound but over the term bound once keyword
    /// tokenized, so the two caps are reachable independently.
    fn over_term_bound() -> String {
        "a".repeat(MAX_TERM_BYTES + 1)
    }

    /// A value whose token count exceeds the token bound under the
    /// whitespace tokenizer, and equals it after one token is removed.
    fn over_token_bound() -> String {
        "a ".repeat(MAX_TOKENS_PER_FIELD)
    }

    #[test]
    fn the_three_v1_default_names_resolve_to_their_kinds() {
        assert_eq!(Analyzer::resolve("KeywordAnalyzer").unwrap().kind(), &AnalyzerKind::Keyword);
        assert_eq!(
            Analyzer::resolve("LowerCaseKeywordAnalyzer").unwrap().kind(),
            &AnalyzerKind::LowercaseKeyword
        );
        assert_eq!(
            Analyzer::resolve("TerraBaseStandardAnalyzer").unwrap().kind(),
            &AnalyzerKind::Standard
        );
    }

    #[test]
    fn an_unknown_analyzer_name_fails_closed() {
        // V1 accepts assembly-qualified type names and compiled custom
        // analyzer names; V2's closed set refuses them.
        for name in [
            "SimpleAnalyzer",
            "Lucene.Net.Analysis.SimpleAnalyzer",
            "StopAnalyzer",
            "NGramAnalyzer",
            "MyNamespace.MyCustomAnalyzer",
            "analyzer/1.0:standard+lowercase",
        ] {
            assert_eq!(AnalyzerKind::resolve(name), Err(AnalyzerError::UnknownAnalyzer), "{name}");
        }
    }

    #[test]
    fn a_malformed_analyzer_name_is_invalid_before_it_is_unknown() {
        for name in ["", " ", " padded ", "\u{0}bad", &"x".repeat(MAX_ANALYZER_NAME_BYTES + 1)] {
            assert_eq!(
                AnalyzerKind::resolve(name),
                Err(AnalyzerError::InvalidAnalyzerName),
                "name must be refused as malformed"
            );
        }
    }

    #[test]
    fn a_composite_analyzer_is_bounded_and_duplicate_free() {
        assert!(
            AnalyzerKind::compose(TokenizerKind::Standard, vec![TokenFilterKind::LowerCase])
                .is_ok()
        );
        assert_eq!(
            AnalyzerKind::compose(
                TokenizerKind::Whitespace,
                vec![TokenFilterKind::LowerCase; MAX_TOKEN_FILTERS + 1]
            )
            .err(),
            Some(AnalyzerError::TooManyFilters)
        );
        assert_eq!(
            AnalyzerKind::compose(
                TokenizerKind::Whitespace,
                vec![TokenFilterKind::LowerCase, TokenFilterKind::LowerCase]
            )
            .err(),
            Some(AnalyzerError::DuplicateFilter)
        );
    }

    #[test]
    fn the_analyzer_identity_round_trips_the_named_set() {
        for name in ["KeywordAnalyzer", "LowerCaseKeywordAnalyzer", "TerraBaseStandardAnalyzer"] {
            assert_eq!(Analyzer::resolve(name).unwrap().identity(), name);
        }
        assert_eq!(
            AnalyzerKind::compose(TokenizerKind::Standard, vec![TokenFilterKind::LowerCase])
                .unwrap()
                .identity(),
            "analyzer:standard+lowercase"
        );
    }

    #[test]
    fn keyword_analysis_emits_one_term_spanning_the_value() {
        let vector = Analyzer::resolve("KeywordAnalyzer")
            .unwrap()
            .analyze("productNumber", "AB-117  b", TermVectorMode::WithPositionsAndOffsets)
            .unwrap();
        assert_eq!(vector.entries().len(), 1);
        let entry = &vector.entries()[0];
        // The exact default preserves case and interior spacing: one term,
        // the whole value, byte for byte.
        assert_eq!(entry.term(), "AB-117  b");
        assert_eq!(entry.frequency(), 1);
        assert_eq!(entry.positions(), [0]);
        assert_eq!(entry.offsets(), [TermOffset::try_new(0, 9).unwrap()]);
    }

    #[test]
    fn an_empty_value_produces_an_empty_term_vector() {
        for name in ["KeywordAnalyzer", "LowerCaseKeywordAnalyzer", "TerraBaseStandardAnalyzer"] {
            let vector = Analyzer::resolve(name)
                .unwrap()
                .analyze("body", "", TermVectorMode::WithPositionsAndOffsets)
                .unwrap();
            assert!(vector.entries().is_empty(), "{name} must emit no term for an empty value");
            assert_eq!(vector.total_tokens(), 0);
        }
    }

    #[test]
    fn whitespace_analysis_records_positions_and_byte_offsets() {
        let analyzer = Analyzer::try_new(
            AnalyzerKind::compose(TokenizerKind::Whitespace, Vec::new()).unwrap(),
        )
        .unwrap();
        let vector = analyzer
            .analyze("body", "  the quick   fox", TermVectorMode::WithPositionsAndOffsets)
            .unwrap();
        // Positions are token indices; offsets are byte ranges over the
        // original value, irregular spacing included.
        assert_eq!(vector.entries()[0].term(), "fox");
        assert_eq!(vector.entries()[0].positions(), [2]);
        assert_eq!(vector.entries()[0].offsets(), [TermOffset::try_new(14, 17).unwrap()]);
        assert_eq!(vector.entries()[1].term(), "quick");
        assert_eq!(vector.entries()[1].offsets(), [TermOffset::try_new(6, 11).unwrap()]);
        assert_eq!(vector.entries()[2].term(), "the");
        assert_eq!(vector.entries()[2].offsets(), [TermOffset::try_new(2, 5).unwrap()]);
    }

    #[test]
    fn standard_analysis_splits_on_non_alphanumeric_runs() {
        let vector = Analyzer::resolve("TerraBaseStandardAnalyzer")
            .unwrap()
            .analyze("body", "O'Neill's 42nd-street", TermVectorMode::Frequencies)
            .unwrap();
        let terms: Vec<&str> = vector.entries().iter().map(TermVectorEntry::term).collect();
        assert_eq!(terms, ["42nd", "neill", "o", "s", "street"]);
    }

    #[test]
    fn the_lowercase_filter_merges_case_variants_into_one_entry() {
        let vector = Analyzer::resolve("TerraBaseStandardAnalyzer")
            .unwrap()
            .analyze("body", "The the THE quick", TermVectorMode::WithPositionsAndOffsets)
            .unwrap();
        assert_eq!(vector.frequency_of("the"), Some(3));
        let entry = &vector.entries()[vector.entries().len() - 1];
        assert_eq!(entry.term(), "the");
        assert_eq!(entry.positions(), [0, 1, 2]);
        // Offsets keep the original casing's byte ranges.
        assert_eq!(entry.offsets()[0], TermOffset::try_new(0, 3).unwrap());
        assert_eq!(entry.offsets()[2], TermOffset::try_new(8, 11).unwrap());
    }

    #[test]
    fn a_mode_that_does_not_carry_positions_carries_none() {
        let vector = Analyzer::resolve("TerraBaseStandardAnalyzer")
            .unwrap()
            .analyze("body", "one two two", TermVectorMode::WithPositions)
            .unwrap();
        assert!(vector.mode().has_positions());
        assert!(!vector.mode().has_offsets());
        for entry in vector.entries() {
            assert_eq!(entry.positions().len(), entry.frequency() as usize);
            assert!(entry.offsets().is_empty());
        }
    }

    #[test]
    fn analysis_is_deterministic() {
        let analyzer = Analyzer::resolve("TerraBaseStandardAnalyzer").unwrap();
        let left =
            analyzer.analyze("body", "Same Input same", TermVectorMode::WithPositionsAndOffsets);
        let right =
            analyzer.analyze("body", "Same Input same", TermVectorMode::WithPositionsAndOffsets);
        assert_eq!(left, right);
    }

    #[test]
    fn the_input_cap_refuses_before_tokenizing() {
        let analyzer = Analyzer::resolve("LowerCaseKeywordAnalyzer").unwrap();
        let at_cap = "a".repeat(MAX_ANALYZED_VALUE_BYTES);
        assert_eq!(
            analyzer.analyze("body", &at_cap, TermVectorMode::Frequencies).err(),
            Some(AnalyzerError::TermTooLarge),
            "a whole-value term at the input cap is over the term bound, not under it"
        );
        let over_cap = "a".repeat(MAX_ANALYZED_VALUE_BYTES + 1);
        assert_eq!(
            analyzer.analyze("body", &over_cap, TermVectorMode::Frequencies),
            Err(AnalyzerError::InputTooLarge)
        );
    }

    #[test]
    fn the_term_cap_is_reachable_independently_of_the_input_cap() {
        let analyzer = Analyzer::resolve("KeywordAnalyzer").unwrap();
        assert_eq!(
            analyzer.analyze("body", &over_term_bound(), TermVectorMode::Frequencies),
            Err(AnalyzerError::TermTooLarge)
        );
    }

    #[test]
    fn the_token_cap_refuses_and_its_boundary_is_exactly_valid() {
        let analyzer = Analyzer::try_new(
            AnalyzerKind::compose(TokenizerKind::Whitespace, Vec::new()).unwrap(),
        )
        .unwrap();
        // One trailing space keeps the last token: the count is exactly
        // at the cap.
        let at_cap = "a ".repeat(MAX_TOKENS_PER_FIELD);
        assert_eq!(
            analyzer.analyze("body", &at_cap, TermVectorMode::Frequencies).unwrap().total_tokens()
                as usize,
            MAX_TOKENS_PER_FIELD
        );
        // One more token crosses it.
        let over_cap = format!("{}b", over_token_bound());
        assert_eq!(
            analyzer.analyze("body", &over_cap, TermVectorMode::Frequencies),
            Err(AnalyzerError::TokenBoundExceeded)
        );
    }

    #[test]
    fn a_pathological_input_returns_rather_than_recursing() {
        let analyzer = Analyzer::resolve("TerraBaseStandardAnalyzer").unwrap();
        // A maximal run of separators and a maximal run of one character:
        // both walk the value once and return a typed answer.
        let separators = " ".repeat(MAX_ANALYZED_VALUE_BYTES);
        assert_eq!(
            analyzer
                .analyze("body", &separators, TermVectorMode::WithPositionsAndOffsets)
                .unwrap()
                .entries()
                .len(),
            0
        );
        let one_run = "a".repeat(MAX_ANALYZED_VALUE_BYTES);
        assert_eq!(
            analyzer.analyze("body", &one_run, TermVectorMode::Frequencies),
            Err(AnalyzerError::TermTooLarge)
        );
    }

    #[test]
    fn analysis_refuses_a_no_mode_and_an_invalid_field() {
        let analyzer = Analyzer::resolve("KeywordAnalyzer").unwrap();
        assert_eq!(
            analyzer.analyze("body", "value", TermVectorMode::No),
            Err(AnalyzerError::InvalidTermVector)
        );
        for field in ["", " lead", "trail ", "\u{0}bad", &"f".repeat(MAX_FIELD_BYTES + 1)] {
            assert_eq!(
                analyzer.analyze(field, "value", TermVectorMode::Frequencies),
                Err(AnalyzerError::InvalidTermVector),
                "field {field:?} must be refused"
            );
        }
    }

    #[test]
    fn term_vector_modes_round_trip_the_v1_wire_names() {
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
        assert_eq!(TermVectorMode::parse("WITH_OFFSETS"), Err(AnalyzerError::UnknownAnalyzer));
        assert_eq!(TermVectorMode::parse(""), Err(AnalyzerError::UnknownAnalyzer));
        assert!(!TermVectorMode::No.is_stored());
        assert!(TermVectorMode::Frequencies.is_stored());
    }

    #[test]
    fn an_offset_range_is_half_open_and_bounded() {
        assert_eq!(TermOffset::try_new(0, 1), Ok(TermOffset { start: 0, end: 1 }));
        assert_eq!(TermOffset::try_new(1, 1), Err(AnalyzerError::InvalidTermVector));
        assert_eq!(
            TermOffset::try_new(0, u32::try_from(MAX_ANALYZED_VALUE_BYTES).unwrap() + 1),
            Err(AnalyzerError::InvalidTermVector)
        );
    }

    /// One frequencies-only entry of the given frequency.
    fn bare_entry(term: &str, frequency: u32) -> TermVectorEntry {
        TermVectorEntry::try_new(term.to_owned(), frequency, Vec::new(), Vec::new()).unwrap()
    }

    #[test]
    fn an_entry_validates_its_own_shape() {
        assert_eq!(
            TermVectorEntry::try_new(String::new(), 1, Vec::new(), Vec::new()),
            Err(AnalyzerError::InvalidTermVector)
        );
        assert_eq!(
            TermVectorEntry::try_new("x".repeat(MAX_TERM_BYTES + 1), 1, Vec::new(), Vec::new()),
            Err(AnalyzerError::InvalidTermVector)
        );
        assert_eq!(
            TermVectorEntry::try_new("term".to_owned(), 0, Vec::new(), Vec::new()),
            Err(AnalyzerError::InvalidTermVector)
        );
        // A position list that disagrees with the frequency.
        assert_eq!(
            TermVectorEntry::try_new(
                "term".to_owned(),
                2,
                vec![0],
                vec![TermOffset::try_new(0, 1).unwrap()]
            ),
            Err(AnalyzerError::InvalidTermVector)
        );
        // Positions must be strictly increasing token indices.
        assert_eq!(
            TermVectorEntry::try_new(
                "term".to_owned(),
                2,
                vec![1, 0],
                vec![TermOffset::try_new(0, 1).unwrap(), TermOffset::try_new(1, 2).unwrap()]
            ),
            Err(AnalyzerError::InvalidTermVector)
        );
        // Offsets must not overlap.
        assert_eq!(
            TermVectorEntry::try_new(
                "term".to_owned(),
                2,
                vec![0, 1],
                vec![TermOffset::try_new(0, 3).unwrap(), TermOffset::try_new(2, 4).unwrap()]
            ),
            Err(AnalyzerError::InvalidTermVector)
        );
        // A frequency above the token bound.
        let over = u32::try_from(MAX_TOKENS_PER_FIELD).unwrap() + 1;
        assert_eq!(
            TermVectorEntry::try_new("term".to_owned(), over, Vec::new(), Vec::new()),
            Err(AnalyzerError::InvalidTermVector)
        );
    }

    #[test]
    fn a_term_vector_fails_closed_on_unknown_versions() {
        // The entry deliberately violates the mode too: the version check
        // runs first, which is the contract being pinned.
        let inconsistent = TermVectorEntry::try_new(
            "term".to_owned(),
            1,
            vec![0],
            vec![TermOffset::try_new(0, 1).unwrap()],
        )
        .unwrap();
        for version in ["analyzer/0.9", "analyzer/2.0", "analyzer/1", "", "idx/1.0"] {
            assert_eq!(
                TermVector::try_from_parts(
                    version,
                    "body",
                    TermVectorMode::Frequencies,
                    vec![inconsistent.clone()]
                ),
                Err(AnalyzerError::UnknownVersion),
                "version {version:?} must be refused before any other validation"
            );
        }
        let vector =
            TermVector::try_new("body", TermVectorMode::Frequencies, vec![bare_entry("term", 1)])
                .unwrap();
        assert_eq!(vector.contract_version(), ANALYZER_LANGUAGE_VERSION);
        assert!(vector.contract_version().starts_with(ANALYZER_LANGUAGE_PREFIX));
    }

    #[test]
    fn a_term_vector_requires_entries_to_agree_with_its_mode() {
        // Frequencies mode: an entry carrying positions disagrees.
        let positioned = TermVectorEntry::try_new(
            "term".to_owned(),
            1,
            vec![0],
            vec![TermOffset::try_new(0, 1).unwrap()],
        )
        .unwrap();
        assert_eq!(
            TermVector::try_new("body", TermVectorMode::Frequencies, vec![positioned]),
            Err(AnalyzerError::InvalidTermVector)
        );
        let bare = bare_entry("term", 1);
        assert!(
            TermVector::try_new("body", TermVectorMode::Frequencies, vec![bare.clone()]).is_ok()
        );
        // With-positions mode: an entry carrying no positions disagrees.
        assert_eq!(
            TermVector::try_new("body", TermVectorMode::WithPositions, vec![bare.clone()]),
            Err(AnalyzerError::InvalidTermVector)
        );
        // No mode is a field option, not a vector shape.
        assert_eq!(
            TermVector::try_new("body", TermVectorMode::No, vec![bare]),
            Err(AnalyzerError::InvalidTermVector)
        );
    }

    #[test]
    fn a_term_vector_keeps_one_ordered_entry_per_term() {
        assert_eq!(
            TermVector::try_new(
                "body",
                TermVectorMode::Frequencies,
                vec![bare_entry("b", 1), bare_entry("a", 1)]
            ),
            Err(AnalyzerError::InvalidTermVector),
            "entries out of term order must be refused"
        );
        assert_eq!(
            TermVector::try_new(
                "body",
                TermVectorMode::Frequencies,
                vec![bare_entry("a", 1), bare_entry("a", 1)]
            ),
            Err(AnalyzerError::InvalidTermVector),
            "a duplicated term must be refused"
        );
    }

    #[test]
    fn a_term_vector_bounds_its_frequency_mass() {
        let at_bound = u32::try_from(MAX_TOKENS_PER_FIELD).unwrap();
        assert!(
            TermVector::try_new(
                "body",
                TermVectorMode::Frequencies,
                vec![bare_entry("term", at_bound)]
            )
            .is_ok()
        );
        assert_eq!(
            TermVector::try_new(
                "body",
                TermVectorMode::Frequencies,
                vec![bare_entry("term", at_bound), bare_entry("z", 1)]
            ),
            Err(AnalyzerError::InvalidTermVector),
            "the total token mass, not one entry, is the bound"
        );
    }
}
