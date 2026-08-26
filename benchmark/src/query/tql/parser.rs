//! The `tql/1.0` recursive-descent parser.
//!
//! One pass over the token stream with single-token lookahead and no
//! backtracking: every clause and expression position decides from at most
//! the next two tokens. Recursion depth is bounded by [`MAX_NESTING_DEPTH`],
//! so the stack stays bounded on adversarial input, path-segment chains are
//! bounded in the loop that scans them, and every rejection is a positioned
//! parse failure. Clause order is fixed; a misplaced, repeated, or trailing
//! clause is a parse error, per Section 3.1 of the specification.

use super::{
    GrammarVersion,
    ast::{
        BinaryOperator, Direction, Expression, ExpressionKind, FunctionCall, GroupKey, Literal,
        LoadItem, LogicalOperator, NullsOrder, ObjectMember, OrderKey, PageBound, PageBoundKind,
        Paging, PathRoot, PathSegment, Position, Projection, ProjectionItem, ProjectionKind,
        Quantifier, Span, TqlPath, TqlQuery, TqlSource, UnaryOperator,
    },
    lexer::{Keyword, Punct, Token, TokenKind},
};

/// The bound on expression, constructor, and path-segment nesting depth.
///
/// The bound keeps the parser's stack bounded on adversarial input; queries
/// are never legitimately nested this deeply.
pub(super) const MAX_NESTING_DEPTH: usize = 128;

/// A positioned parse rejection; the caller maps it onto the typed
/// `ParseError` class.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) struct ParseFailure {
    /// The position of the offending token or input boundary.
    pub position: Position,
}

/// Parses one token stream into the typed AST of the accepted version.
///
/// The version is the artifact's declared and accepted version; the AST root
/// carries it unchanged.
pub(super) fn parse_query(
    version: GrammarVersion,
    tokens: &[Token],
    end_of_text: Position,
) -> Result<TqlQuery, ParseFailure> {
    let mut parser = Parser { tokens, position: 0, depth: 0 };
    parser.query(version, end_of_text)
}

/// The cursor over the token stream.
struct Parser<'tokens> {
    tokens: &'tokens [Token],
    position: usize,
    depth: usize,
}

impl Parser<'_> {
    /// Parses the full clause sequence.
    fn query(
        &mut self,
        version: GrammarVersion,
        end_of_text: Position,
    ) -> Result<TqlQuery, ParseFailure> {
        let start = self.tokens.first().map_or(end_of_text, |token| token.start);
        let mut projection = match self.peek_keyword() {
            Some(Keyword::Select) => Some(self.select_clause()?),
            _ => None,
        };
        let source = self.source_clause()?;
        if projection.is_none() {
            // An omitted select is the whole-document projection; its marker
            // span is the source start, where the clause would have been.
            projection = Some(Projection {
                span: Span::new(start, start),
                kind: ProjectionKind::WholeDocument,
                distinct: false,
            });
        }
        let loads = match self.peek_keyword() {
            Some(Keyword::Load) => self.load_clause()?,
            _ => Vec::new(),
        };
        let filter = match self.peek_keyword() {
            Some(Keyword::Where) => Some(self.where_clause()?),
            _ => None,
        };
        let grouping = match self.peek_keyword() {
            Some(Keyword::Group) => self.group_clause()?,
            _ => Vec::new(),
        };
        let ordering = match self.peek_keyword() {
            Some(Keyword::Order) => self.order_clause()?,
            _ => Vec::new(),
        };
        // The paging clauses are accepted in their fixed order; a page bound
        // outside it falls through to the trailing-token check below.
        let mut paging = Paging { span: Span::new(start, start), limit: None, offset: None };
        let mut paging_start = None;
        let mut paging_end = None;
        if self.peek_keyword() == Some(Keyword::Limit) {
            let bound = self.page_bound(Keyword::Limit)?;
            paging_start = Some(bound.span.start());
            paging_end = Some(bound.span.end());
            paging.limit = Some(bound);
        }
        if self.peek_keyword() == Some(Keyword::Offset) {
            let bound = self.page_bound(Keyword::Offset)?;
            paging_start.get_or_insert(bound.span.start());
            paging_end = Some(bound.span.end());
            paging.offset = Some(bound);
        }
        if let (Some(start_position), Some(end_position)) = (paging_start, paging_end) {
            paging.span = Span::new(start_position, end_position);
        }
        let includes = match self.peek_keyword() {
            Some(Keyword::Include) => self.include_clause()?,
            _ => Vec::new(),
        };
        // Anything left is a misplaced or repeated clause, or trailing input.
        if let Some(token) = self.tokens.get(self.position) {
            return Err(ParseFailure { position: token.start });
        }
        Ok(TqlQuery {
            grammar_version: version,
            span: Span::new(start, end_of_text),
            projection: projection.expect("the projection was just filled"),
            source,
            loads,
            filter,
            grouping,
            ordering,
            paging,
            includes,
        })
    }

    /// Parses `select [distinct] projection_list`.
    fn select_clause(&mut self) -> Result<Projection, ParseFailure> {
        let start = self.clause_start(Keyword::Select)?;
        let distinct = self.accept_keyword(Keyword::Distinct);
        let mut items = Vec::new();
        loop {
            let item_start = self.current_position();
            if self.accept_punct(Punct::Star) {
                // `*` is the whole-document projection and cannot combine
                // with other items: the AST keeps one closed projection
                // shape, so a mixed list fails closed here.
                if !items.is_empty() || self.peek_is(Punct::Comma) {
                    return Err(ParseFailure { position: item_start });
                }
                let end = self.previous_end();
                return Ok(Projection {
                    span: Span::new(start, end),
                    kind: ProjectionKind::WholeDocument,
                    distinct,
                });
            }
            let expr = self.expression()?;
            let alias = self.accept_alias()?;
            let end = self.previous_end();
            items.push(ProjectionItem { span: Span::new(item_start, end), expr, alias });
            if !self.accept_punct(Punct::Comma) {
                break;
            }
        }
        let end = self.previous_end();
        Ok(Projection { span: Span::new(start, end), kind: ProjectionKind::Items(items), distinct })
    }

    /// Parses `from source [as alias]`.
    fn source_clause(&mut self) -> Result<TqlSource, ParseFailure> {
        let start = self.clause_start(Keyword::From)?;
        let kind = match self.next_kind() {
            Some(TokenKind::AllDocs) => super::ast::SourceKind::AllDocs,
            Some(TokenKind::Word { text, keyword: None }) => {
                super::ast::SourceKind::Collection((*text).clone())
            }
            Some(TokenKind::Quoted(name)) => super::ast::SourceKind::Collection(name.clone()),
            _ => return Err(ParseFailure { position: self.current_position() }),
        };
        let alias = self.accept_alias()?;
        let end = self.previous_end();
        Ok(TqlSource { span: Span::new(start, end), kind, alias })
    }

    /// Parses `load path as alias {, path as alias}`.
    fn load_clause(&mut self) -> Result<Vec<LoadItem>, ParseFailure> {
        self.clause_start(Keyword::Load)?;
        let mut items = Vec::new();
        loop {
            let item_start = self.current_position();
            let path = self.clause_path()?;
            if !self.accept_keyword(Keyword::As) {
                return Err(ParseFailure { position: self.current_position() });
            }
            let Some(alias) = self.accept_name() else {
                return Err(ParseFailure { position: self.current_position() });
            };
            let end = self.previous_end();
            items.push(LoadItem { span: Span::new(item_start, end), path, alias });
            if !self.accept_punct(Punct::Comma) {
                break;
            }
        }
        Ok(items)
    }

    /// Parses `where expression`.
    fn where_clause(&mut self) -> Result<Expression, ParseFailure> {
        self.clause_start(Keyword::Where)?;
        self.expression()
    }

    /// Parses `group by key {, key}`.
    fn group_clause(&mut self) -> Result<Vec<GroupKey>, ParseFailure> {
        self.clause_start(Keyword::Group)?;
        if !self.accept_keyword(Keyword::By) {
            return Err(ParseFailure { position: self.current_position() });
        }
        let mut keys = Vec::new();
        loop {
            let key_start = self.current_position();
            let expr = self.expression()?;
            let alias = self.accept_alias()?;
            let end = self.previous_end();
            keys.push(GroupKey { span: Span::new(key_start, end), expr, alias });
            if !self.accept_punct(Punct::Comma) {
                break;
            }
        }
        Ok(keys)
    }

    /// Parses `order by key {, key}`.
    fn order_clause(&mut self) -> Result<Vec<OrderKey>, ParseFailure> {
        self.clause_start(Keyword::Order)?;
        if !self.accept_keyword(Keyword::By) {
            return Err(ParseFailure { position: self.current_position() });
        }
        let mut keys = Vec::new();
        loop {
            let key_start = self.current_position();
            let expr = self.expression()?;
            let direction = if self.accept_any_keyword(&[Keyword::Asc, Keyword::Ascending]) {
                Some(Direction::Ascending)
            } else if self.accept_any_keyword(&[Keyword::Desc, Keyword::Descending]) {
                Some(Direction::Descending)
            } else {
                None
            };
            let nulls = if self.accept_keyword(Keyword::Nulls) {
                if self.accept_keyword(Keyword::First) {
                    Some(NullsOrder::First)
                } else if self.accept_keyword(Keyword::Last) {
                    Some(NullsOrder::Last)
                } else {
                    return Err(ParseFailure { position: self.current_position() });
                }
            } else {
                None
            };
            let end = self.previous_end();
            keys.push(OrderKey { span: Span::new(key_start, end), expr, direction, nulls });
            if !self.accept_punct(Punct::Comma) {
                break;
            }
        }
        Ok(keys)
    }

    /// Parses `limit|offset page_bound`.
    fn page_bound(&mut self, clause: Keyword) -> Result<PageBound, ParseFailure> {
        self.clause_start(clause)?;
        let start = self.current_position();
        let kind = match self.next_kind() {
            Some(TokenKind::Int(value)) if *value >= 0 => PageBoundKind::Literal(
                u64::try_from(*value).map_err(|_| ParseFailure { position: start })?,
            ),
            Some(TokenKind::Parameter(name)) => PageBoundKind::Parameter(name.clone()),
            _ => return Err(ParseFailure { position: start }),
        };
        let end = self.previous_end();
        Ok(PageBound { span: Span::new(start, end), kind })
    }

    /// Parses `include path {, path}`.
    fn include_clause(&mut self) -> Result<Vec<TqlPath>, ParseFailure> {
        self.clause_start(Keyword::Include)?;
        let mut paths = Vec::new();
        loop {
            paths.push(self.clause_path()?);
            if !self.accept_punct(Punct::Comma) {
                break;
            }
        }
        Ok(paths)
    }

    // -----------------------------------------------------------------------
    // Expressions
    // -----------------------------------------------------------------------

    /// Parses one expression through the full precedence ladder.
    fn expression(&mut self) -> Result<Expression, ParseFailure> {
        self.enter()?;
        let expression = self.or_expression();
        self.exit();
        expression
    }

    fn or_expression(&mut self) -> Result<Expression, ParseFailure> {
        let mut operands = vec![self.and_expression()?];
        while self.accept_keyword(Keyword::Or) {
            operands.push(self.and_expression()?);
        }
        Ok(logical(LogicalOperator::Or, operands))
    }

    fn and_expression(&mut self) -> Result<Expression, ParseFailure> {
        let mut operands = vec![self.not_expression()?];
        while self.accept_keyword(Keyword::And) {
            operands.push(self.not_expression()?);
        }
        Ok(logical(LogicalOperator::And, operands))
    }

    fn not_expression(&mut self) -> Result<Expression, ParseFailure> {
        let start = self.current_position();
        if self.peek_keyword() == Some(Keyword::Not) {
            self.enter()?;
            self.position += 1;
            let operand = self.not_expression();
            self.exit();
            let operand = operand?;
            let end = self.previous_end();
            return Ok(Expression {
                span: Span::new(start, end),
                kind: ExpressionKind::Not { operand: Box::new(operand) },
            });
        }
        self.comparison()
    }

    fn comparison(&mut self) -> Result<Expression, ParseFailure> {
        let left = self.additive()?;
        let operator = match self.peek_punct() {
            Some(Punct::Assign) => Some(BinaryOperator::Equals),
            Some(Punct::NotEqual) => Some(BinaryOperator::NotEquals),
            Some(Punct::Less) => Some(BinaryOperator::Less),
            Some(Punct::LessEqual) => Some(BinaryOperator::LessOrEqual),
            Some(Punct::Greater) => Some(BinaryOperator::Greater),
            Some(Punct::GreaterEqual) => Some(BinaryOperator::GreaterOrEqual),
            _ => None,
        };
        if let Some(op) = operator {
            self.position += 1;
            let right = self.additive()?;
            return Ok(binary(op, left, right));
        }
        if self.peek_keyword() == Some(Keyword::Between) {
            self.position += 1;
            let low = self.additive()?;
            if !self.accept_keyword(Keyword::And) {
                return Err(ParseFailure { position: self.current_position() });
            }
            let high = self.additive()?;
            let span = Span::new(left.span.start(), high.span.end());
            return Ok(Expression {
                span,
                kind: ExpressionKind::Between {
                    expr: Box::new(left),
                    low: Box::new(low),
                    high: Box::new(high),
                },
            });
        }
        let quantifier = if self.peek_keyword() == Some(Keyword::All) {
            self.position += 1;
            if self.peek_keyword() != Some(Keyword::In) {
                return Err(ParseFailure { position: self.current_position() });
            }
            self.position += 1;
            Quantifier::All
        } else if self.peek_keyword() == Some(Keyword::In) {
            self.position += 1;
            Quantifier::Any
        } else {
            return Ok(left);
        };
        if !self.accept_punct(Punct::LeftParen) {
            return Err(ParseFailure { position: self.current_position() });
        }
        let mut list = Vec::new();
        if !self.peek_is(Punct::RightParen) {
            loop {
                list.push(self.expression()?);
                if !self.accept_punct(Punct::Comma) {
                    break;
                }
            }
        }
        if !self.accept_punct(Punct::RightParen) {
            return Err(ParseFailure { position: self.current_position() });
        }
        let span = Span::new(left.span.start(), self.previous_end());
        Ok(Expression { span, kind: ExpressionKind::In { quantifier, expr: Box::new(left), list } })
    }

    fn additive(&mut self) -> Result<Expression, ParseFailure> {
        let mut left = self.multiplicative()?;
        loop {
            let op = match self.peek_punct() {
                Some(Punct::Plus) => BinaryOperator::Add,
                Some(Punct::Minus) => BinaryOperator::Subtract,
                _ => return Ok(left),
            };
            self.position += 1;
            let right = self.multiplicative()?;
            left = binary(op, left, right);
        }
    }

    fn multiplicative(&mut self) -> Result<Expression, ParseFailure> {
        let mut left = self.unary()?;
        loop {
            let op = match self.peek_punct() {
                Some(Punct::Star) => BinaryOperator::Multiply,
                Some(Punct::Slash) => BinaryOperator::Divide,
                Some(Punct::Percent) => BinaryOperator::Modulo,
                _ => return Ok(left),
            };
            self.position += 1;
            let right = self.unary()?;
            left = binary(op, left, right);
        }
    }

    fn unary(&mut self) -> Result<Expression, ParseFailure> {
        let start = self.current_position();
        let op = match self.peek_punct() {
            Some(Punct::Minus) => Some(UnaryOperator::Negate),
            Some(Punct::Plus) => Some(UnaryOperator::Plus),
            _ => None,
        };
        if let Some(op) = op {
            self.enter()?;
            self.position += 1;
            let operand = self.unary();
            self.exit();
            let operand = operand?;
            let end = self.previous_end();
            return Ok(Expression {
                span: Span::new(start, end),
                kind: ExpressionKind::Unary { op, operand: Box::new(operand) },
            });
        }
        self.primary()
    }

    fn primary(&mut self) -> Result<Expression, ParseFailure> {
        let start = self.current_position();
        match self.next_kind().cloned() {
            Some(TokenKind::Int(value)) => {
                Ok(self.literal_expression(start, Literal::Integer(value)))
            }
            Some(TokenKind::Num(value)) => {
                Ok(self.literal_expression(start, Literal::Number(value)))
            }
            Some(TokenKind::Str(value)) => {
                Ok(self.literal_expression(start, Literal::String(value)))
            }
            Some(TokenKind::Word { keyword: Some(Keyword::True), .. }) => {
                Ok(self.literal_expression(start, Literal::Boolean(true)))
            }
            Some(TokenKind::Word { keyword: Some(Keyword::False), .. }) => {
                Ok(self.literal_expression(start, Literal::Boolean(false)))
            }
            Some(TokenKind::Word { keyword: Some(Keyword::Null), .. }) => {
                Ok(self.literal_expression(start, Literal::Null))
            }
            Some(TokenKind::Parameter(name)) => {
                let end = self.previous_end();
                Ok(Expression {
                    span: Span::new(start, end),
                    kind: ExpressionKind::ParameterRef(name),
                })
            }
            Some(TokenKind::Metadata(field)) => {
                self.continue_path(start, PathRoot::Metadata(field))
            }
            Some(TokenKind::Word { text, keyword: None }) => self.word_primary(start, text),
            Some(TokenKind::Quoted(name)) => self.continue_path(start, PathRoot::Member(name)),
            Some(TokenKind::Punct(Punct::LeftParen)) => {
                self.enter()?;
                let inner = self.expression();
                self.exit();
                let inner = inner?;
                if !self.accept_punct(Punct::RightParen) {
                    return Err(ParseFailure { position: self.current_position() });
                }
                let end = self.previous_end();
                Ok(Expression { span: Span::new(start, end), kind: inner.kind })
            }
            Some(TokenKind::Punct(Punct::LeftBracket)) => self.array_constructor(start),
            Some(TokenKind::Punct(Punct::LeftBrace)) => self.object_constructor(start),
            _ => Err(ParseFailure { position: start }),
        }
    }

    /// Distinguishes a function call from a member-rooted path for one bare
    /// word in primary position: a dotted name chain followed by `(` is the
    /// `identifier { "." identifier }` call name (for example
    /// `vector.search`); anything else is a path whose root is the word.
    fn word_primary(&mut self, start: Position, first: String) -> Result<Expression, ParseFailure> {
        // Find the end of the maximal dotted word chain starting at `first`;
        // only a chain the next token closes with `(` is a call name.
        let mut chain_end = self.position;
        while matches!(
            self.tokens.get(chain_end).map(|token| &token.kind),
            Some(TokenKind::Punct(Punct::Dot))
        ) && matches!(
            self.tokens.get(chain_end + 1).map(|token| &token.kind),
            Some(TokenKind::Word { keyword: None, .. })
        ) {
            chain_end += 2;
        }
        if !matches!(
            self.tokens.get(chain_end).map(|token| &token.kind),
            Some(TokenKind::Punct(Punct::LeftParen))
        ) {
            return self.continue_path(start, PathRoot::Member(first));
        }
        let mut name = first;
        while self.position < chain_end {
            if let Some(TokenKind::Word { text, .. }) =
                self.tokens.get(self.position).map(|token| token.kind.clone())
            {
                name.push('.');
                name.push_str(&text);
            }
            self.position += 1;
        }
        if !self.accept_punct(Punct::LeftParen) {
            return Err(ParseFailure { position: self.current_position() });
        }
        self.enter()?;
        let mut args = Vec::new();
        if !self.peek_is(Punct::RightParen) {
            loop {
                args.push(self.call_argument()?);
                if !self.accept_punct(Punct::Comma) {
                    break;
                }
            }
        }
        self.exit();
        if !self.accept_punct(Punct::RightParen) {
            return Err(ParseFailure { position: self.current_position() });
        }
        let end = self.previous_end();
        // A function call is neither navigable nor indexable in tql/1.0.
        if matches!(self.peek_punct(), Some(Punct::Dot | Punct::LeftBracket)) {
            return Err(ParseFailure { position: self.current_position() });
        }
        Ok(Expression {
            span: Span::new(start, end),
            kind: ExpressionKind::Call(FunctionCall { span: Span::new(start, end), name, args }),
        })
    }

    /// Parses one array constructor in primary position.
    fn array_constructor(&mut self, start: Position) -> Result<Expression, ParseFailure> {
        self.enter()?;
        let mut elements = Vec::new();
        if !self.peek_is(Punct::RightBracket) {
            loop {
                elements.push(self.expression()?);
                if !self.accept_punct(Punct::Comma) {
                    break;
                }
            }
        }
        self.exit();
        if !self.accept_punct(Punct::RightBracket) {
            return Err(ParseFailure { position: self.current_position() });
        }
        let end = self.previous_end();
        Ok(Expression { span: Span::new(start, end), kind: ExpressionKind::ArrayCtor { elements } })
    }

    /// Parses one object constructor in primary position.
    fn object_constructor(&mut self, start: Position) -> Result<Expression, ParseFailure> {
        self.enter()?;
        let mut members = Vec::new();
        if !self.peek_is(Punct::RightBrace) {
            loop {
                let member_start = self.current_position();
                let Some(key) = self.accept_name() else {
                    return Err(ParseFailure { position: member_start });
                };
                if !self.accept_punct(Punct::Colon) {
                    return Err(ParseFailure { position: self.current_position() });
                }
                let value = self.expression()?;
                let end = self.previous_end();
                members.push(ObjectMember { span: Span::new(member_start, end), key, value });
                if !self.accept_punct(Punct::Comma) {
                    break;
                }
            }
        }
        self.exit();
        if !self.accept_punct(Punct::RightBrace) {
            return Err(ParseFailure { position: self.current_position() });
        }
        let end = self.previous_end();
        Ok(Expression { span: Span::new(start, end), kind: ExpressionKind::ObjectCtor { members } })
    }

    /// Parses one call argument: an expression, or the bare `and`/`or`
    /// combiner of the gated search functions (Section 8.1 of the
    /// specification).
    fn call_argument(&mut self) -> Result<Expression, ParseFailure> {
        let start = self.current_position();
        let combiner = match self.peek_keyword() {
            Some(Keyword::And) => Some("and"),
            Some(Keyword::Or) => Some("or"),
            _ => None,
        };
        if let Some(combiner) = combiner {
            self.position += 1;
            let end = self.previous_end();
            return Ok(Expression {
                span: Span::new(start, end),
                kind: ExpressionKind::Literal(Literal::String(combiner.to_owned())),
            });
        }
        self.expression()
    }

    // -----------------------------------------------------------------------
    // Paths
    // -----------------------------------------------------------------------

    /// Parses one path in clause position (`load`, `include`).
    fn clause_path(&mut self) -> Result<TqlPath, ParseFailure> {
        let start = self.current_position();
        let root = match self.next_kind().cloned() {
            Some(TokenKind::Word { text, keyword: None }) => PathRoot::Member(text),
            Some(TokenKind::Quoted(name)) => PathRoot::Member(name),
            Some(TokenKind::Metadata(field)) => PathRoot::Metadata(field),
            _ => return Err(ParseFailure { position: start }),
        };
        self.path_segments(start, root)
    }

    /// Parses the segments of one path whose root was just consumed.
    fn continue_path(
        &mut self,
        start: Position,
        root: PathRoot,
    ) -> Result<Expression, ParseFailure> {
        let path = self.path_segments(start, root)?;
        let end = path.span.end();
        Ok(Expression { span: Span::new(start, end), kind: ExpressionKind::Path(path) })
    }

    /// Scans the `.member`, `[index]`, and `[]` segments of one path.
    ///
    /// Segments are consumed only when complete: a dot or bracket that does
    /// not continue a well-formed segment stops the scan with the cursor on
    /// the dot or bracket, so the enclosing clause reports the malformed
    /// position instead of silently dropping tokens.
    fn path_segments(&mut self, start: Position, root: PathRoot) -> Result<TqlPath, ParseFailure> {
        let mut segments = Vec::new();
        loop {
            if segments.len() >= MAX_NESTING_DEPTH {
                return Err(ParseFailure { position: start });
            }
            if self.peek_is(Punct::Dot)
                && let Some(member) = self.member_after_dot()
            {
                segments.push(PathSegment::Member(member));
                continue;
            }
            if let Some(segment) = self.bracket_segment() {
                segments.push(segment);
                continue;
            }
            break;
        }
        let end = self.previous_end();
        Ok(TqlPath { span: Span::new(start, end), root, segments })
    }

    /// Consumes `.name` and returns the member name, leaving the cursor
    /// untouched when the dot does not continue a well-formed member.
    fn member_after_dot(&mut self) -> Option<String> {
        match self.tokens.get(self.position + 1).map(|token| token.kind.clone()) {
            Some(TokenKind::Word { text, keyword: None }) => {
                self.position += 2;
                Some(text)
            }
            Some(TokenKind::Quoted(name)) => {
                self.position += 2;
                Some(name)
            }
            _ => None,
        }
    }

    /// Consumes `[]` or `[index]` and returns the segment, leaving the
    /// cursor untouched when the bracket does not continue a well-formed
    /// segment.
    fn bracket_segment(&mut self) -> Option<PathSegment> {
        if !self.peek_is(Punct::LeftBracket) {
            return None;
        }
        match self.tokens.get(self.position + 1).map(|token| token.kind.clone()) {
            Some(TokenKind::Punct(Punct::RightBracket)) => {
                self.position += 2;
                Some(PathSegment::AnyElement)
            }
            Some(TokenKind::Int(value)) if value >= 0 => {
                let index = u32::try_from(value).ok()?;
                if !matches!(
                    self.tokens.get(self.position + 2).map(|token| &token.kind),
                    Some(TokenKind::Punct(Punct::RightBracket))
                ) {
                    return None;
                }
                self.position += 3;
                Some(PathSegment::ArrayIndex(index))
            }
            _ => None,
        }
    }

    // -----------------------------------------------------------------------
    // Cursor helpers
    // -----------------------------------------------------------------------

    fn peek_keyword(&self) -> Option<Keyword> {
        match self.tokens.get(self.position).map(|token| &token.kind) {
            Some(TokenKind::Word { keyword, .. }) => *keyword,
            _ => None,
        }
    }

    fn accept_keyword(&mut self, keyword: Keyword) -> bool {
        if self.peek_keyword() == Some(keyword) {
            self.position += 1;
            return true;
        }
        false
    }

    fn accept_any_keyword(&mut self, keywords: &[Keyword]) -> bool {
        keywords.iter().any(|keyword| self.accept_keyword(*keyword))
    }

    fn peek_is(&self, punct: Punct) -> bool {
        self.peek_punct() == Some(punct)
    }

    fn peek_punct(&self) -> Option<Punct> {
        match self.tokens.get(self.position).map(|token| &token.kind) {
            Some(TokenKind::Punct(seen)) => Some(*seen),
            _ => None,
        }
    }

    fn accept_punct(&mut self, punct: Punct) -> bool {
        if self.peek_punct() == Some(punct) {
            self.position += 1;
            return true;
        }
        false
    }

    /// Advances over one token and returns its kind, when one exists.
    fn next_kind(&mut self) -> Option<&TokenKind> {
        let kind = self.tokens.get(self.position).map(|token| &token.kind);
        if kind.is_some() {
            self.position += 1;
        }
        kind
    }

    /// Consumes one alias position (`as name`) and returns the name.
    fn accept_alias(&mut self) -> Result<Option<String>, ParseFailure> {
        if !self.accept_keyword(Keyword::As) {
            return Ok(None);
        }
        match self.next_kind().cloned() {
            Some(TokenKind::Word { text, keyword: None }) => Ok(Some(text)),
            Some(TokenKind::Quoted(name)) => Ok(Some(name)),
            _ => Err(ParseFailure { position: self.current_position() }),
        }
    }

    /// Consumes one member name in bare or quoted form, when one is next.
    fn accept_name(&mut self) -> Option<String> {
        match self.tokens.get(self.position).map(|token| token.kind.clone()) {
            Some(TokenKind::Word { text, keyword: None }) => {
                self.position += 1;
                Some(text)
            }
            Some(TokenKind::Quoted(name)) => {
                self.position += 1;
                Some(name)
            }
            _ => None,
        }
    }

    /// Consumes one clause keyword and returns its start position.
    fn clause_start(&mut self, keyword: Keyword) -> Result<Position, ParseFailure> {
        let start = self.current_position();
        if !self.accept_keyword(keyword) {
            return Err(ParseFailure { position: start });
        }
        Ok(start)
    }

    /// Returns the start of the next token, or the boundary past the final
    /// token at end of input.
    fn current_position(&self) -> Position {
        self.tokens
            .get(self.position)
            .map_or(self.tokens.last().map_or(Position::new(0, 1, 1), |token| token.end), |token| {
                token.start
            })
    }

    /// Returns the end of the most recently consumed token.
    fn previous_end(&self) -> Position {
        self.tokens
            .get(self.position.wrapping_sub(1))
            .map_or(Position::new(0, 1, 1), |token| token.end)
    }

    fn enter(&mut self) -> Result<(), ParseFailure> {
        self.depth += 1;
        if self.depth > MAX_NESTING_DEPTH {
            return Err(ParseFailure { position: self.current_position() });
        }
        Ok(())
    }

    fn exit(&mut self) {
        self.depth = self.depth.saturating_sub(1);
    }

    fn literal_expression(&self, start: Position, literal: Literal) -> Expression {
        let end = self.previous_end();
        Expression { span: Span::new(start, end), kind: ExpressionKind::Literal(literal) }
    }
}

/// Builds one logical node, collapsing a single operand.
fn logical(op: LogicalOperator, operands: Vec<Expression>) -> Expression {
    if operands.len() == 1 {
        return operands.into_iter().next().expect("one operand was passed by the caller");
    }
    let span = Span::new(
        operands.first().expect("one operand was passed by the caller").span.start(),
        operands.last().expect("one operand was passed by the caller").span.end(),
    );
    Expression { span, kind: ExpressionKind::Logical { op, operands } }
}

/// Builds one binary node.
fn binary(op: BinaryOperator, left: Expression, right: Expression) -> Expression {
    let span = Span::new(left.span.start(), right.span.end());
    Expression {
        span,
        kind: ExpressionKind::Binary { op, left: Box::new(left), right: Box::new(right) },
    }
}
