//! The private V1-compatible query grammar.
//!
//! This module defines the exact small text subset W3-2 accepts through
//! [`QueryInput::V1Compatible`](crate::operations::QueryInput::V1Compatible)
//! and rejects as [`QueryInputError::UnsupportedGrammar`] everywhere else. It
//! is a V1-inspired subset, not a claim about V1's grammar, and it is the only
//! parser in the crate: the same function validates the public
//! [`V1QueryText`](crate::operations::V1QueryText) wrapper and normalizes the
//! text into the same model as the typed form.
//!
//! The grammar is, in this fixed clause order, each clause optional:
//!
//! ```text
//! query      := "from" collection
//!              ["where" condition *("and" condition)]
//!              ["order by" key *("," key)]
//!              ["limit" uint]
//!              ["offset" uint]
//!              ["select" field *("," field)]
//! condition  := field op literal | "exists" "(" field ")"
//!             | "missing" "(" field ")"
//! op         := "=" | "!=" | "<" | "<=" | ">" | ">="
//! key        := field ["asc" | "desc"]
//! literal    := "'" text "'" | integer | "true" | "false" | "null"
//! ```
//!
//! Keywords are lowercase and exactly as written. Anything else — uppercase
//! keywords, SQL shapes such as `SELECT * FROM …`, parentheses, `or`,
//! double-quoted strings, `*`, misplaced clauses, or trailing tokens — is
//! `UnsupportedGrammar`. An empty or whitespace-only text is `EmptyText`, and
//! a structurally valid text whose collection or field name fails identifier
//! validation is `InvalidIdentifier`.

use super::spec::{
    OrderDirection, OrderingKey, QueryCondition, QueryOrdering, QueryPaging, QueryPredicate,
    QueryProjection, QuerySource, StalenessMode, StalenessPolicy, validate_field_name,
};
use crate::operations::QueryInputError;
use crate::types::{CollectionName, DocumentValue};

/// One lexical token of the V1-compatible subset.
#[derive(Clone, Debug, PartialEq)]
enum Token {
    Word(String),
    Integer(i64),
    Text(String),
    Punct(&'static str),
}

/// Splits one text into the token stream of the subset.
fn tokenize(text: &str) -> Result<Vec<Token>, QueryInputError> {
    let mut tokens = Vec::new();
    let mut rest = text;
    while let Some(position) = rest.find(|character: char| !character.is_whitespace()) {
        rest = &rest[position..];
        let leading = rest.chars().next().expect("a non-whitespace character");
        let punct = if rest.starts_with("!=") {
            Some("!=")
        } else if rest.starts_with("<=") {
            Some("<=")
        } else if rest.starts_with(">=") {
            Some(">=")
        } else if matches!(leading, '=' | '<' | '>' | '(' | ')' | ',') {
            Some(&rest[..leading.len_utf8()])
        } else {
            None
        };
        if let Some(punct) = punct {
            tokens.push(Token::Punct(match punct {
                "=" => "=",
                "!=" => "!=",
                "<" => "<",
                "<=" => "<=",
                ">" => ">",
                ">=" => ">=",
                "(" => "(",
                ")" => ")",
                _ => ",",
            }));
            rest = &rest[punct.len()..];
            continue;
        }
        if leading == '\'' {
            let end = rest[1..].find('\'').ok_or(QueryInputError::UnsupportedGrammar)?;
            tokens.push(Token::Text(rest[1..=end].to_owned()));
            rest = &rest[end + 2..];
            continue;
        }
        let length = rest
            .find(|character: char| {
                character.is_whitespace()
                    || matches!(character, '=' | '<' | '>' | '(' | ')' | ',' | '\'')
            })
            .unwrap_or(rest.len());
        let word = &rest[..length];
        let negative = word.starts_with('-');
        let digits = if negative { &word[1..] } else { word };
        if !digits.is_empty() && digits.bytes().all(|byte| byte.is_ascii_digit()) {
            tokens.push(Token::Integer(
                word.parse::<i64>().map_err(|_| QueryInputError::UnsupportedGrammar)?,
            ));
        } else if negative || word.contains(|character: char| !is_word_character(character)) {
            return Err(QueryInputError::UnsupportedGrammar);
        } else {
            tokens.push(Token::Word(word.to_owned()));
        }
        rest = &rest[length..];
    }
    Ok(tokens)
}

/// Reports whether one character may appear in a word of the subset.
const fn is_word_character(character: char) -> bool {
    character.is_ascii_alphanumeric() || matches!(character, '_' | '-')
}

/// The cursor over the token stream.
struct Parser {
    tokens: Vec<Token>,
    position: usize,
}

impl Parser {
    fn peek(&self) -> Option<&Token> {
        self.tokens.get(self.position)
    }

    fn next(&mut self) -> Option<Token> {
        let token = self.tokens.get(self.position).cloned();
        if token.is_some() {
            self.position += 1;
        }
        token
    }

    fn expect_punct(&mut self, punct: &'static str) -> Result<(), QueryInputError> {
        match self.next() {
            Some(Token::Punct(seen)) if seen == punct => Ok(()),
            _ => Err(QueryInputError::UnsupportedGrammar),
        }
    }

    fn accept_word(&mut self, word: &str) -> bool {
        if let Some(Token::Word(seen)) = self.peek()
            && seen == word
        {
            self.position += 1;
            return true;
        }
        false
    }

    fn expect_word(&mut self) -> Result<String, QueryInputError> {
        match self.next() {
            Some(Token::Word(word)) => Ok(word),
            _ => Err(QueryInputError::UnsupportedGrammar),
        }
    }

    fn finish(&self) -> Result<(), QueryInputError> {
        if self.position == self.tokens.len() {
            Ok(())
        } else {
            Err(QueryInputError::UnsupportedGrammar)
        }
    }
}

/// Parses one validated collection or field identifier.
fn identifier(word: String) -> Result<String, QueryInputError> {
    validate_field_name(&word).map_err(|_| QueryInputError::InvalidIdentifier)?;
    Ok(word)
}

/// Parses and validates the text into the clauses of the subset.
///
/// The clauses are returned as the typed values of the specification module,
/// so the caller composes them into the one normalized model exactly as the
/// typed form does.
pub(crate) fn parse_clauses(
    text: &str,
) -> Result<
    (QuerySource, QueryPredicate, QueryOrdering, QueryPaging, QueryProjection),
    QueryInputError,
> {
    if text.trim().is_empty() {
        return Err(QueryInputError::EmptyText);
    }
    let mut parser = Parser { tokens: tokenize(text)?, position: 0 };
    if !parser.accept_word("from") {
        return Err(QueryInputError::UnsupportedGrammar);
    }
    let collection = identifier(parser.expect_word()?).and_then(|name| {
        CollectionName::try_new(&name).map_err(|_| QueryInputError::InvalidIdentifier)
    })?;
    let source =
        QuerySource::try_new(collection).map_err(|_| QueryInputError::InvalidIdentifier)?;

    let mut conditions = Vec::new();
    if parser.accept_word("where") {
        conditions.push(parse_condition(&mut parser)?);
        while parser.accept_word("and") {
            conditions.push(parse_condition(&mut parser)?);
        }
    }
    let predicate =
        QueryPredicate::all(conditions).map_err(|_| QueryInputError::UnsupportedGrammar)?;

    let mut keys = Vec::new();
    if parser.accept_word("order") {
        if !parser.accept_word("by") {
            return Err(QueryInputError::UnsupportedGrammar);
        }
        keys.push(parse_key(&mut parser)?);
        while matches!(parser.peek(), Some(Token::Punct(","))) {
            parser.position += 1;
            keys.push(parse_key(&mut parser)?);
        }
    }
    let ordering = QueryOrdering::try_new(keys).map_err(|_| QueryInputError::UnsupportedGrammar)?;

    let mut limit = None;
    if parser.accept_word("limit") {
        let value = parse_uint(&mut parser)?;
        limit = Some(u32::try_from(value).map_err(|_| QueryInputError::UnsupportedGrammar)?);
    }
    let mut offset = 0;
    if parser.accept_word("offset") {
        offset = parse_uint(&mut parser)?;
    }
    let paging =
        QueryPaging::try_new(offset, limit).map_err(|_| QueryInputError::UnsupportedGrammar)?;

    let mut projection = QueryProjection::whole_document();
    if parser.accept_word("select") {
        let mut names = vec![identifier(parser.expect_word()?)?];
        while matches!(parser.peek(), Some(Token::Punct(","))) {
            parser.position += 1;
            names.push(identifier(parser.expect_word()?)?);
        }
        let borrowed: Vec<&str> = names.iter().map(String::as_str).collect();
        projection =
            QueryProjection::fields(&borrowed).map_err(|_| QueryInputError::InvalidIdentifier)?;
    }
    parser.finish()?;
    Ok((source, predicate, ordering, paging, projection))
}

/// Parses one condition of the subset.
fn parse_condition(parser: &mut Parser) -> Result<QueryCondition, QueryInputError> {
    let head = parser.peek().cloned();
    if let Some(Token::Word(word)) = head
        && (word == "exists" || word == "missing")
    {
        parser.position += 1;
        parser.expect_punct("(")?;
        let field = identifier(parser.expect_word()?)?;
        parser.expect_punct(")")?;
        return Ok(if word == "exists" {
            QueryCondition::Exists { field }
        } else {
            QueryCondition::Missing { field }
        });
    }
    let field = identifier(parser.expect_word()?)?;
    let operator = match parser.next() {
        Some(Token::Punct("=")) => "=",
        Some(Token::Punct("!=")) => "!=",
        Some(Token::Punct("<")) => "<",
        Some(Token::Punct("<=")) => "<=",
        Some(Token::Punct(">")) => ">",
        Some(Token::Punct(">=")) => ">=",
        _ => return Err(QueryInputError::UnsupportedGrammar),
    };
    let value = match parser.next() {
        Some(Token::Text(text)) => DocumentValue::String(text),
        Some(Token::Integer(number)) => DocumentValue::Integer(number),
        Some(Token::Word(word)) if word == "true" => DocumentValue::Boolean(true),
        Some(Token::Word(word)) if word == "false" => DocumentValue::Boolean(false),
        Some(Token::Word(word)) if word == "null" => DocumentValue::Null,
        _ => return Err(QueryInputError::UnsupportedGrammar),
    };
    Ok(match operator {
        "=" => QueryCondition::Equals { field, value },
        "!=" => QueryCondition::NotEquals { field, value },
        "<" => QueryCondition::LessThan { field, value },
        "<=" => QueryCondition::LessOrEqual { field, value },
        ">" => QueryCondition::GreaterThan { field, value },
        _ => QueryCondition::GreaterOrEqual { field, value },
    })
}

/// Parses one ordering key with its optional direction.
fn parse_key(parser: &mut Parser) -> Result<OrderingKey, QueryInputError> {
    let field = identifier(parser.expect_word()?)?;
    let direction = if parser.accept_word("asc") {
        OrderDirection::Ascending
    } else if parser.accept_word("desc") {
        OrderDirection::Descending
    } else {
        OrderDirection::Ascending
    };
    OrderingKey::try_new(&field, direction).map_err(|_| QueryInputError::InvalidIdentifier)
}

/// Parses one non-negative page number.
fn parse_uint(parser: &mut Parser) -> Result<u64, QueryInputError> {
    match parser.next() {
        Some(Token::Integer(value)) if value >= 0 => Ok(u64::try_from(value).unwrap_or(u64::MAX)),
        _ => Err(QueryInputError::UnsupportedGrammar),
    }
}

/// Validates one text against the subset without keeping the clauses.
#[allow(dead_code, reason = "the public V1QueryText::try_new validation is the integrator action")]
pub(crate) fn validate(text: &str) -> Result<(), QueryInputError> {
    parse_clauses(text).map(|_| ())
}

/// The default staleness policy of the V1-compatible form: strict with no
/// required index namespace, so the form executes index-less. The subset has
/// no staleness clause and makes no V1 staleness claim.
pub(crate) fn default_staleness() -> StalenessPolicy {
    StalenessPolicy::try_new(StalenessMode::Strict, Vec::new())
        .expect("an empty strict policy is always valid")
}
