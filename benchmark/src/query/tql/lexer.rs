//! The zero-dependency TQL tokenizer (grammar version `tql/1.0`).
//!
//! The tokenizer is one linear scan over the input: every character is
//! visited once, positions (byte, line, column) are computed while scanning,
//! and every malformed shape — unterminated strings, comments, and quoted
//! identifiers, unknown `@` pseudo-fields, invalid number tails — is one
//! positioned rejection. There is no lookahead beyond the current token and
//! no backtracking.

use super::ast::MetadataField;

/// One reserved word of Section 3.3 of the specification.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum Keyword {
    /// `select`
    Select,
    /// `distinct`
    Distinct,
    /// `from`
    From,
    /// `where`
    Where,
    /// `group`
    Group,
    /// `by`
    By,
    /// `order`
    Order,
    /// `limit`
    Limit,
    /// `offset`
    Offset,
    /// `load`
    Load,
    /// `include`
    Include,
    /// `as`
    As,
    /// `and`
    And,
    /// `or`
    Or,
    /// `not`
    Not,
    /// `in`
    In,
    /// `all`
    All,
    /// `between`
    Between,
    /// `nulls`
    Nulls,
    /// `first`
    First,
    /// `last`
    Last,
    /// `asc`
    Asc,
    /// `desc`
    Desc,
    /// `ascending`
    Ascending,
    /// `descending`
    Descending,
    /// `true`
    True,
    /// `false`
    False,
    /// `null`
    Null,
}

impl Keyword {
    /// Maps one scanned word onto its reserved word, when the word is one.
    pub(super) fn of_word(word: &str) -> Option<Self> {
        Some(match word.to_ascii_lowercase().as_str() {
            "select" => Self::Select,
            "distinct" => Self::Distinct,
            "from" => Self::From,
            "where" => Self::Where,
            "group" => Self::Group,
            "by" => Self::By,
            "order" => Self::Order,
            "limit" => Self::Limit,
            "offset" => Self::Offset,
            "load" => Self::Load,
            "include" => Self::Include,
            "as" => Self::As,
            "and" => Self::And,
            "or" => Self::Or,
            "not" => Self::Not,
            "in" => Self::In,
            "all" => Self::All,
            "between" => Self::Between,
            "nulls" => Self::Nulls,
            "first" => Self::First,
            "last" => Self::Last,
            "asc" => Self::Asc,
            "desc" => Self::Desc,
            "ascending" => Self::Ascending,
            "descending" => Self::Descending,
            "true" => Self::True,
            "false" => Self::False,
            "null" => Self::Null,
            _ => return None,
        })
    }
}

/// One punctuation token.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum Punct {
    /// `=`
    Assign,
    /// `!=` or `<>`
    NotEqual,
    /// `<`
    Less,
    /// `<=`
    LessEqual,
    /// `>`
    Greater,
    /// `>=`
    GreaterEqual,
    /// `+`
    Plus,
    /// `-`
    Minus,
    /// `*`
    Star,
    /// `/`
    Slash,
    /// `%`
    Percent,
    /// `(`
    LeftParen,
    /// `)`
    RightParen,
    /// `[`
    LeftBracket,
    /// `]`
    RightBracket,
    /// `{`
    LeftBrace,
    /// `}`
    RightBrace,
    /// `,`
    Comma,
    /// `:`
    Colon,
    /// `.`
    Dot,
}

/// One lexical token with its source positions.
#[derive(Clone, Debug, PartialEq)]
pub(super) struct Token {
    /// The token kind.
    pub kind: TokenKind,
    /// The position of the first character.
    pub start: super::ast::Position,
    /// The position just past the last character.
    pub end: super::ast::Position,
}

/// The token kinds of `tql/1.0`.
#[derive(Clone, Debug, PartialEq)]
pub(super) enum TokenKind {
    /// A bare word with its reserved-word class, when the word is one.
    Word { text: String, keyword: Option<Keyword> },
    /// A quoted identifier with `""` escapes resolved.
    Quoted(String),
    /// A single-quoted string literal with `''` escapes resolved.
    Str(String),
    /// An integer literal that fits the engine's integer domain.
    Int(i64),
    /// A number literal outside the integer domain.
    Num(f64),
    /// A `$name` parameter reference.
    Parameter(String),
    /// One metadata pseudo-field.
    Metadata(MetadataField),
    /// The `@all_docs` source token.
    AllDocs,
    /// One punctuation token.
    Punct(Punct),
}

/// A positioned lexical rejection; the caller maps it onto the typed
/// `ParseError` class.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) struct LexError {
    /// The position of the offending character.
    pub position: super::ast::Position,
}

/// Splits one text into its token stream.
///
/// The input is bounded by the shared query text bound before this function
/// runs, so the scan is linear in a bounded input. An empty token stream is
/// legal here; the parser rejects it as empty text.
pub(super) fn tokenize(text: &str) -> Result<Vec<Token>, LexError> {
    let mut lexer = Lexer { text, chars: text.char_indices().collect(), index: 0 };
    let mut tokens = Vec::new();
    while let Some(character) = lexer.peek() {
        if character.is_whitespace() {
            lexer.advance();
            continue;
        }
        if lexer.consume_comment()? {
            continue;
        }
        let start = lexer.position();
        let kind = lexer.scan_token()?;
        let end = lexer.position();
        tokens.push(Token { kind, start, end });
    }
    Ok(tokens)
}

/// The scanning cursor. `chars` is the pre-collected `(byte, character)` list
/// of the bounded input, so position arithmetic is index-local.
struct Lexer<'text> {
    text: &'text str,
    chars: Vec<(usize, char)>,
    index: usize,
}

impl Lexer<'_> {
    fn peek(&self) -> Option<char> {
        self.chars.get(self.index).map(|(_, character)| *character)
    }

    fn peek_second(&self) -> Option<char> {
        self.chars.get(self.index + 1).map(|(_, character)| *character)
    }

    fn advance(&mut self) -> &mut Self {
        self.index += 1;
        self
    }

    /// Returns the position of the character at the cursor: the position a
    /// token starting here reports, and the boundary a token ending here
    /// reports. At end of input the position is just past the final
    /// character.
    fn position(&self) -> super::ast::Position {
        self.position_at(self.index)
    }

    /// Computes the position of one character index.
    fn position_at(&self, index: usize) -> super::ast::Position {
        let (line, column) = self.line_column_at(index);
        let byte = self.chars.get(index).map_or(self.text.len(), |(byte, _)| *byte);
        super::ast::Position::new(byte, line, column)
    }

    /// Computes the 1-based line and column of the character at `index` from
    /// the characters before it.
    fn line_column_at(&self, index: usize) -> (u32, u32) {
        let mut line = 1_u32;
        let mut column = 1_u32;
        for &(_, character) in &self.chars[..index.min(self.chars.len())] {
            if character == '\n' {
                line += 1;
                column = 1;
            } else {
                column += 1;
            }
        }
        (line, column)
    }

    /// Consumes one comment when the cursor is at a comment opener.
    fn consume_comment(&mut self) -> Result<bool, LexError> {
        if self.peek() != Some('/') {
            return Ok(false);
        }
        match self.peek_second() {
            Some('/') => {
                self.advance().advance();
                while let Some(character) = self.peek() {
                    if character == '\n' {
                        self.advance();
                        return Ok(true);
                    }
                    self.advance();
                }
                Ok(true)
            }
            Some('*') => {
                let opener = self.position();
                self.advance().advance();
                while let Some(character) = self.peek() {
                    if character == '*' && self.peek_second() == Some('/') {
                        self.advance().advance();
                        return Ok(true);
                    }
                    self.advance();
                }
                Err(LexError { position: opener })
            }
            _ => Ok(false),
        }
    }

    /// Scans exactly one token; the cursor is at a non-whitespace character.
    fn scan_token(&mut self) -> Result<TokenKind, LexError> {
        let start = self.position();
        let character = self.peek().expect("the caller checked for a character");
        if let Some(punct) = self.punct(character) {
            return Ok(TokenKind::Punct(punct));
        }
        match character {
            '\'' => self.scan_quoted('\'').map(TokenKind::Str),
            '"' => self.scan_quoted('"').map(TokenKind::Quoted),
            '$' => self.scan_parameter(start),
            '@' => self.scan_metadata(),
            _ if character.is_ascii_digit() => self.scan_number(),
            _ if character.is_ascii_alphabetic() || character == '_' => Ok(self.scan_word()),
            _ => Err(LexError { position: start }),
        }
    }

    /// Scans one punctuation token, consuming multi-character operators
    /// before single-character ones.
    fn punct(&mut self, character: char) -> Option<Punct> {
        let second = self.peek_second();
        let pair = match (character, second) {
            ('!', Some('=')) | ('<', Some('>')) => Some(Punct::NotEqual),
            ('<', Some('=')) => Some(Punct::LessEqual),
            ('>', Some('=')) => Some(Punct::GreaterEqual),
            _ => None,
        };
        if let Some(punct) = pair {
            self.advance().advance();
            return Some(punct);
        }
        let single = match character {
            '=' => Punct::Assign,
            '<' => Punct::Less,
            '>' => Punct::Greater,
            '+' => Punct::Plus,
            '-' => Punct::Minus,
            '*' => Punct::Star,
            '/' => Punct::Slash,
            '%' => Punct::Percent,
            '(' => Punct::LeftParen,
            ')' => Punct::RightParen,
            '[' => Punct::LeftBracket,
            ']' => Punct::RightBracket,
            '{' => Punct::LeftBrace,
            '}' => Punct::RightBrace,
            ',' => Punct::Comma,
            ':' => Punct::Colon,
            '.' => Punct::Dot,
            _ => return None,
        };
        self.advance();
        Some(single)
    }

    /// Scans one quoted form; `''` (strings) and `""` (quoted identifiers)
    /// escape one quote character. Control characters are rejected.
    fn scan_quoted(&mut self, quote: char) -> Result<String, LexError> {
        let start = self.position();
        self.advance();
        let mut value = String::new();
        while let Some(character) = self.peek() {
            if character == quote {
                if self.peek_second() == Some(quote) {
                    value.push(quote);
                    self.advance().advance();
                    continue;
                }
                self.advance();
                return Ok(value);
            }
            if character.is_control() {
                return Err(LexError { position: self.position() });
            }
            value.push(character);
            self.advance();
        }
        Err(LexError { position: start })
    }

    /// Scans one `$name` parameter reference.
    fn scan_parameter(&mut self, start: super::ast::Position) -> Result<TokenKind, LexError> {
        self.advance();
        let name = self.scan_identifier_tail();
        if name.is_empty() {
            return Err(LexError { position: start });
        }
        Ok(TokenKind::Parameter(name))
    }

    /// Scans one `@`-form: the five metadata pseudo-fields or `@all_docs`.
    fn scan_metadata(&mut self) -> Result<TokenKind, LexError> {
        let start = self.position();
        self.advance();
        let word = self.scan_identifier_tail();
        let kind = match word.as_str() {
            "id" => TokenKind::Metadata(MetadataField::Id),
            "collection" => TokenKind::Metadata(MetadataField::Collection),
            "last_modified" => TokenKind::Metadata(MetadataField::LastModified),
            "change_vector" => TokenKind::Metadata(MetadataField::ChangeVector),
            "metadata" => TokenKind::Metadata(MetadataField::MetadataRoot),
            "all_docs" => TokenKind::AllDocs,
            _ => return Err(LexError { position: start }),
        };
        Ok(kind)
    }

    /// Scans one number literal per the EBNF: digits, an optional fraction
    /// with at least one digit, and an optional exponent with at least one
    /// digit. A pure digit run that fits the integer domain stays `Int`.
    fn scan_number(&mut self) -> Result<TokenKind, LexError> {
        let start = self.position();
        let digits = self.scan_digit_run();
        let mut is_float = false;
        let mut text = digits.clone();
        if self.peek() == Some('.') && self.peek_second().is_some_and(|next| next.is_ascii_digit())
        {
            is_float = true;
            text.push('.');
            self.advance();
            text.push_str(&self.scan_digit_run());
        }
        if matches!(self.peek(), Some('e' | 'E')) {
            let exponent_start = self.position();
            self.advance();
            let mut exponent = String::new();
            if matches!(self.peek(), Some('+' | '-')) {
                exponent.push(self.peek().expect("checked above"));
                self.advance();
            }
            let exponent_digits = self.scan_digit_run();
            if exponent_digits.is_empty() {
                return Err(LexError { position: exponent_start });
            }
            exponent.push_str(&exponent_digits);
            is_float = true;
            text.push('e');
            text.push_str(&exponent);
        }
        if !is_float && let Ok(value) = digits.parse::<i64>() {
            return Ok(TokenKind::Int(value));
        }
        text.parse::<f64>().map(TokenKind::Num).map_err(|_| LexError { position: start })
    }

    /// Consumes one maximal run of ASCII digits.
    fn scan_digit_run(&mut self) -> String {
        let mut digits = String::new();
        while let Some(character) = self.peek() {
            if character.is_ascii_digit() {
                digits.push(character);
                self.advance();
            } else {
                break;
            }
        }
        digits
    }

    /// Scans one bare word: an ASCII letter or underscore first, then the
    /// identifier character set including `-`.
    fn scan_word(&mut self) -> TokenKind {
        let mut text = String::new();
        while let Some(character) = self.peek() {
            if character.is_ascii_alphanumeric() || character == '_' || character == '-' {
                text.push(character);
                self.advance();
            } else {
                break;
            }
        }
        let keyword = Keyword::of_word(&text);
        TokenKind::Word { text, keyword }
    }

    /// Consumes the identifier tail after a sigil (`$`, `@`): the identifier
    /// character set without the leading-position restriction.
    fn scan_identifier_tail(&mut self) -> String {
        let mut text = String::new();
        while let Some(character) = self.peek() {
            if character.is_ascii_alphanumeric() || character == '_' {
                text.push(character);
                self.advance();
            } else {
                break;
            }
        }
        text
    }
}
