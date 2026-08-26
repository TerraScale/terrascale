//! The typed, versioned TQL AST (grammar version `tql/1.0`).
//!
//! These are the closed node kinds of Section 11 of `docs/tql/spec.md`. Every
//! node carries a [`Span`]; the query root additionally carries the
//! [`GrammarVersion`](super::GrammarVersion) of the artifact it was parsed
//! from, so an AST is never attributable to no version. The AST contains no
//! unvalidated strings: identifiers, collection names, and function names are
//! validated during parsing and checking, and parameters are stored as named
//! binding slots, never inlined text.

/// One source position: a byte offset plus its 1-based line and column.
///
/// Columns count characters, not bytes, so a position stays meaningful in
/// text with multi-byte characters inside quoted identifiers and strings.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Position {
    byte: usize,
    line: u32,
    column: u32,
}

impl Position {
    /// Creates one position from its parts.
    #[must_use]
    pub const fn new(byte: usize, line: u32, column: u32) -> Self {
        Self { byte, line, column }
    }

    /// Returns the byte offset from the start of the parse input.
    #[must_use]
    pub const fn byte(self) -> usize {
        self.byte
    }

    /// Returns the 1-based line.
    #[must_use]
    pub const fn line(self) -> u32 {
        self.line
    }

    /// Returns the 1-based column in characters.
    #[must_use]
    pub const fn column(self) -> u32 {
        self.column
    }
}

/// The half-open source range of one AST node.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Span {
    start: Position,
    end: Position,
}

impl Span {
    /// Creates one span from explicit start and end positions.
    #[must_use]
    pub const fn new(start: Position, end: Position) -> Self {
        Self { start, end }
    }

    /// Returns the position of the first character of the node.
    #[must_use]
    pub const fn start(self) -> Position {
        self.start
    }

    /// Returns the position just past the last character of the node.
    #[must_use]
    pub const fn end(self) -> Position {
        self.end
    }
}

/// The typed root of one parsed TQL query.
#[derive(Clone, Debug, PartialEq)]
pub struct TqlQuery {
    /// The grammar version the artifact declared and the parser accepted.
    pub grammar_version: super::GrammarVersion,
    /// The span of the whole query text.
    pub span: Span,
    /// The projection clause; an omitted `select` is the whole-document form.
    pub projection: Projection,
    /// The one mandatory source clause.
    pub source: TqlSource,
    /// The reference-resolution bindings, in declaration order.
    pub loads: Vec<LoadItem>,
    /// The optional filter.
    pub filter: Option<Expression>,
    /// The grouping keys, in declaration order.
    pub grouping: Vec<GroupKey>,
    /// The ordering keys, in declaration order.
    pub ordering: Vec<OrderKey>,
    /// The paging request; bounds are pre-validated where statically decidable.
    pub paging: Paging,
    /// The eager-fetch paths, in declaration order.
    pub includes: Vec<TqlPath>,
}

impl TqlQuery {
    /// Returns the canonical one-line rendering used by the golden vectors.
    ///
    /// The outline is a stable projection of the typed AST for `tql/1.0`: the
    /// same AST always renders the same outline, and any grammar or AST shape
    /// change is a grammar-version change that forces the vectors to move.
    #[must_use]
    pub fn outline(&self) -> String {
        format!(
            "(query {} (select {} {}) (from {}) {} {} {} {} {} {} {})",
            self.grammar_version.as_str(),
            if self.projection.distinct { "distinct" } else { "plain" },
            render_projection(&self.projection),
            render_source(self),
            clause(
                "load",
                self.loads
                    .iter()
                    .map(|load| format!("{} as {}", render_path(&load.path), load.alias))
                    .collect::<Vec<_>>(),
            ),
            clause(
                "where",
                vec![self.filter.as_ref().map_or_else(|| "-".to_owned(), render_expression)]
            ),
            clause(
                "group",
                self.grouping
                    .iter()
                    .map(|key| match &key.alias {
                        Some(alias) => format!("{} as {}", render_expression(&key.expr), alias),
                        None => render_expression(&key.expr),
                    })
                    .collect::<Vec<_>>(),
            ),
            clause("order", self.ordering.iter().map(render_order_key).collect::<Vec<_>>()),
            clause(
                "limit",
                vec![self.paging.limit.as_ref().map_or_else(|| "-".to_owned(), render_bound)]
            ),
            clause(
                "offset",
                vec![self.paging.offset.as_ref().map_or_else(|| "-".to_owned(), render_bound)]
            ),
            clause("include", self.includes.iter().map(render_path).collect::<Vec<_>>()),
        )
    }
}

/// The source clause: one collection or `@all_docs`, with an optional alias.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TqlSource {
    /// The span of the source clause.
    pub span: Span,
    /// The addressed source.
    pub kind: SourceKind,
    /// The alias introduced by `as`, when present.
    pub alias: Option<String>,
}

/// The addressed source of a query.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum SourceKind {
    /// One named collection.
    Collection(String),
    /// Every collection, addressed through `@all_docs`.
    AllDocs,
}

/// The projection clause.
#[derive(Clone, Debug, PartialEq)]
pub struct Projection {
    /// The span of the projection clause.
    pub span: Span,
    /// The projection shape.
    pub kind: ProjectionKind,
    /// Whether `distinct` deduplicates the projected rows.
    pub distinct: bool,
}

/// The projection shape.
#[derive(Clone, Debug, PartialEq)]
pub enum ProjectionKind {
    /// The whole stored document (`select *` or an omitted `select`).
    WholeDocument,
    /// The listed projection items.
    Items(Vec<ProjectionItem>),
}

/// One projection item: an expression with an optional output alias.
#[derive(Clone, Debug, PartialEq)]
pub struct ProjectionItem {
    /// The span of the item.
    pub span: Span,
    /// The projected expression.
    pub expr: Expression,
    /// The output key introduced by `as`, when present.
    pub alias: Option<String>,
}

/// One reference-resolution binding of the `load` clause.
#[derive(Clone, Debug, PartialEq)]
pub struct LoadItem {
    /// The span of the item.
    pub span: Span,
    /// The path read as a referenced document id.
    pub path: TqlPath,
    /// The alias bound to the referenced document.
    pub alias: String,
}

/// One grouping key of the `group by` clause.
#[derive(Clone, Debug, PartialEq)]
pub struct GroupKey {
    /// The span of the key.
    pub span: Span,
    /// The grouping expression.
    pub expr: Expression,
    /// The key name introduced by `as`, when present.
    pub alias: Option<String>,
}

/// One ordering key of the `order by` clause.
#[derive(Clone, Debug, PartialEq)]
pub struct OrderKey {
    /// The span of the key with its options.
    pub span: Span,
    /// The ordering expression.
    pub expr: Expression,
    /// The direction, normalized from `asc`/`ascending`/`desc`/`descending`.
    pub direction: Option<Direction>,
    /// The explicit nulls placement, when present.
    pub nulls: Option<NullsOrder>,
}

/// The direction of one ordering key.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Direction {
    /// Smallest value first.
    Ascending,
    /// Largest value first.
    Descending,
}

/// The explicit nulls placement of one ordering key.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum NullsOrder {
    /// Null and missing sort before every value.
    First,
    /// Null and missing sort after every value.
    Last,
}

/// The paging clause: optional pre-validated bounds.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Paging {
    /// The span from the first to the last page-bound token.
    pub span: Span,
    /// The page-size bound (`limit >= 1`), when present.
    pub limit: Option<PageBound>,
    /// The page-skip bound (`offset >= 0`), when present.
    pub offset: Option<PageBound>,
}

/// One page bound: a literal or a named parameter slot.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PageBound {
    /// The span of the bound.
    pub span: Span,
    /// The bound shape.
    pub kind: PageBoundKind,
}

/// The bound shape.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum PageBoundKind {
    /// An unsigned integer literal.
    Literal(u64),
    /// A named parameter binding slot.
    Parameter(String),
}

/// One typed expression node.
#[derive(Clone, Debug, PartialEq)]
pub struct Expression {
    /// The span of the expression.
    pub span: Span,
    /// The expression kind.
    pub kind: ExpressionKind,
}

/// The closed expression kinds of the typed expression system.
#[derive(Clone, Debug, PartialEq)]
pub enum ExpressionKind {
    /// A literal value.
    Literal(Literal),
    /// A named parameter binding slot.
    ParameterRef(String),
    /// A document path with a member or metadata root.
    Path(TqlPath),
    /// A registry function call.
    Call(FunctionCall),
    /// A unary sign operator.
    Unary {
        /// The operator.
        op: UnaryOperator,
        /// The operand.
        operand: Box<Expression>,
    },
    /// A binary arithmetic or comparison operator.
    Binary {
        /// The operator.
        op: BinaryOperator,
        /// The left operand.
        left: Box<Expression>,
        /// The right operand.
        right: Box<Expression>,
    },
    /// A `between` range test.
    Between {
        /// The tested expression.
        expr: Box<Expression>,
        /// The inclusive lower bound.
        low: Box<Expression>,
        /// The inclusive upper bound.
        high: Box<Expression>,
    },
    /// An `in` or `all in` membership test.
    In {
        /// The quantifier over a listed value set.
        quantifier: Quantifier,
        /// The tested expression.
        expr: Box<Expression>,
        /// The listed values.
        list: Vec<Expression>,
    },
    /// A conjunction or disjunction of two or more operands.
    Logical {
        /// The operator.
        op: LogicalOperator,
        /// The operands in source order.
        operands: Vec<Expression>,
    },
    /// A negation.
    Not {
        /// The negated predicate.
        operand: Box<Expression>,
    },
    /// A typed object constructor.
    ObjectCtor {
        /// The members in source order.
        members: Vec<ObjectMember>,
    },
    /// A typed array constructor.
    ArrayCtor {
        /// The elements in source order.
        elements: Vec<Expression>,
    },
}

/// One member of an object constructor.
#[derive(Clone, Debug, PartialEq)]
pub struct ObjectMember {
    /// The span of the member.
    pub span: Span,
    /// The output key.
    pub key: String,
    /// The member expression.
    pub value: Expression,
}

/// A typed literal.
#[derive(Clone, Debug, PartialEq)]
pub enum Literal {
    /// The absent value.
    Null,
    /// A boolean.
    Boolean(bool),
    /// An integer literal that fits the engine's integer domain.
    Integer(i64),
    /// A number literal with a fraction or exponent, or an integer literal
    /// beyond the integer domain.
    Number(f64),
    /// A single-quoted string literal with `''` escapes resolved.
    String(String),
}

/// A unary operator.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum UnaryOperator {
    /// Numeric negation.
    Negate,
    /// Explicit positive sign.
    Plus,
}

/// A binary operator, arithmetic or comparison.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BinaryOperator {
    /// Addition.
    Add,
    /// Subtraction.
    Subtract,
    /// Multiplication.
    Multiply,
    /// Division.
    Divide,
    /// Remainder.
    Modulo,
    /// Structural equality.
    Equals,
    /// Structural inequality (`!=` and `<>`).
    NotEquals,
    /// Less than.
    Less,
    /// Less than or equal.
    LessOrEqual,
    /// Greater than.
    Greater,
    /// Greater than or equal.
    GreaterOrEqual,
}

impl BinaryOperator {
    /// Returns the canonical symbol used by the outline rendering.
    #[must_use]
    pub const fn symbol(self) -> &'static str {
        match self {
            Self::Add => "+",
            Self::Subtract => "-",
            Self::Multiply => "*",
            Self::Divide => "/",
            Self::Modulo => "%",
            Self::Equals => "=",
            Self::NotEquals => "!=",
            Self::Less => "<",
            Self::LessOrEqual => "<=",
            Self::Greater => ">",
            Self::GreaterOrEqual => ">=",
        }
    }
}

/// The quantifier of an `in` test.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Quantifier {
    /// Any listed value matches (`in`).
    Any,
    /// Every listed value must match (`all in`).
    All,
}

/// A logical operator.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum LogicalOperator {
    /// Conjunction.
    And,
    /// Disjunction.
    Or,
}

/// One typed document path.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TqlPath {
    /// The span of the path.
    pub span: Span,
    /// The path root: a member or a metadata pseudo-field.
    pub root: PathRoot,
    /// The navigation segments after the root.
    pub segments: Vec<PathSegment>,
}

/// The root of a path.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum PathRoot {
    /// A document member (bare or quoted identifier).
    Member(String),
    /// An engine metadata pseudo-field.
    Metadata(MetadataField),
}

/// One navigation segment of a path.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum PathSegment {
    /// A member navigation (bare or quoted identifier).
    Member(String),
    /// A positional array navigation.
    ArrayIndex(u32),
    /// An explicit any-element array navigation.
    AnyElement,
}

/// The metadata pseudo-fields of Section 6.3 of the specification.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MetadataField {
    /// `@id`: the document identity.
    Id,
    /// `@collection`: the owning collection name.
    Collection,
    /// `@last_modified`: the last modification timestamp.
    LastModified,
    /// `@change_vector`: the change vector string.
    ChangeVector,
    /// `@metadata`: the metadata object root.
    MetadataRoot,
}

impl MetadataField {
    /// Returns the pseudo-field spelling.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Id => "@id",
            Self::Collection => "@collection",
            Self::LastModified => "@last_modified",
            Self::ChangeVector => "@change_vector",
            Self::MetadataRoot => "@metadata",
        }
    }
}

/// One registry function call.
#[derive(Clone, Debug, PartialEq)]
pub struct FunctionCall {
    /// The span of the call including its parentheses.
    pub span: Span,
    /// The dotted function name.
    pub name: String,
    /// The arguments in source order.
    pub args: Vec<Expression>,
}

/// Renders one clause: `(name parts...)`, or `(name)` when there are no
/// parts, so empty clauses never end in a dangling space.
#[allow(
    clippy::needless_pass_by_value,
    reason = "the renderer collects each clause's parts once and hands them over"
)]
fn clause(name: &str, parts: Vec<String>) -> String {
    if parts.is_empty() {
        return format!("({name})");
    }
    format!("({name} {})", parts.join(";"))
}

/// Renders the projection shape.
fn render_projection(projection: &Projection) -> String {
    match &projection.kind {
        ProjectionKind::WholeDocument => "*".to_owned(),
        ProjectionKind::Items(items) => items
            .iter()
            .map(|item| match &item.alias {
                Some(alias) => format!("{} as {}", render_expression(&item.expr), alias),
                None => render_expression(&item.expr),
            })
            .collect::<Vec<_>>()
            .join(";"),
    }
}

/// Renders the source clause.
fn render_source(query: &TqlQuery) -> String {
    let base = match &query.source.kind {
        SourceKind::Collection(name) => name.clone(),
        SourceKind::AllDocs => "@all_docs".to_owned(),
    };
    match &query.source.alias {
        Some(alias) => format!("{base} as {alias}"),
        None => base,
    }
}

/// Renders one ordering key with its options.
fn render_order_key(key: &OrderKey) -> String {
    let mut rendered = render_expression(&key.expr);
    if let Some(direction) = key.direction {
        rendered.push_str(match direction {
            Direction::Ascending => " asc",
            Direction::Descending => " desc",
        });
    }
    if let Some(nulls) = key.nulls {
        rendered.push_str(match nulls {
            NullsOrder::First => " nulls first",
            NullsOrder::Last => " nulls last",
        });
    }
    rendered
}

/// Renders one page bound.
fn render_bound(bound: &PageBound) -> String {
    match &bound.kind {
        PageBoundKind::Literal(value) => value.to_string(),
        PageBoundKind::Parameter(name) => format!("${name}"),
    }
}

/// Renders one expression in the canonical outline form.
pub(super) fn render_expression(expression: &Expression) -> String {
    match &expression.kind {
        ExpressionKind::Literal(literal) => render_literal(literal),
        ExpressionKind::ParameterRef(name) => format!("${name}"),
        ExpressionKind::Path(path) => render_path(path),
        ExpressionKind::Call(call) => {
            let args: Vec<String> = call.args.iter().map(render_expression).collect();
            format!("{}({})", call.name, args.join(","))
        }
        ExpressionKind::Unary { op, operand } => {
            format!("({} {})", op.symbol(), render_expression(operand))
        }
        ExpressionKind::Binary { op, left, right } => {
            format!("({} {} {})", op.symbol(), render_expression(left), render_expression(right))
        }
        ExpressionKind::Between { expr, low, high } => format!(
            "(between {} {} {})",
            render_expression(expr),
            render_expression(low),
            render_expression(high)
        ),
        ExpressionKind::In { quantifier, expr, list } => {
            let mut rendered = match quantifier {
                Quantifier::Any => format!("(in-any {}", render_expression(expr)),
                Quantifier::All => format!("(in-all {}", render_expression(expr)),
            };
            for item in list {
                rendered.push(' ');
                rendered.push_str(&render_expression(item));
            }
            rendered.push(')');
            rendered
        }
        ExpressionKind::Logical { op, operands } => {
            let mut rendered = format!("({}", op.symbol());
            for operand in operands {
                rendered.push(' ');
                rendered.push_str(&render_expression(operand));
            }
            rendered.push(')');
            rendered
        }
        ExpressionKind::Not { operand } => format!("(not {})", render_expression(operand)),
        ExpressionKind::ObjectCtor { members } => {
            let rendered: Vec<String> = members
                .iter()
                .map(|member| format!("{}:{}", member.key, render_expression(&member.value)))
                .collect();
            format!("{{{}}}", rendered.join(","))
        }
        ExpressionKind::ArrayCtor { elements } => {
            let rendered: Vec<String> = elements.iter().map(render_expression).collect();
            format!("[{}]", rendered.join(","))
        }
    }
}

impl UnaryOperator {
    /// Returns the canonical symbol used by the outline rendering.
    #[must_use]
    pub const fn symbol(self) -> &'static str {
        match self {
            Self::Negate => "-",
            Self::Plus => "+",
        }
    }
}

impl LogicalOperator {
    /// Returns the canonical symbol used by the outline rendering.
    #[must_use]
    pub const fn symbol(self) -> &'static str {
        match self {
            Self::And => "and",
            Self::Or => "or",
        }
    }
}

/// Renders one literal.
fn render_literal(literal: &Literal) -> String {
    match literal {
        Literal::Null => "null".to_owned(),
        Literal::Boolean(value) => value.to_string(),
        Literal::Integer(value) => format!("int:{value}"),
        Literal::Number(value) => format!("num:{value}"),
        Literal::String(value) => format!("str:'{}'", value.replace('\'', "''")),
    }
}

/// Renders one path.
fn render_path(path: &TqlPath) -> String {
    let mut rendered = match &path.root {
        PathRoot::Member(name) => name.clone(),
        PathRoot::Metadata(field) => field.as_str().to_owned(),
    };
    for segment in &path.segments {
        match segment {
            PathSegment::Member(name) => {
                rendered.push('.');
                rendered.push_str(name);
            }
            PathSegment::ArrayIndex(index) => {
                let index = index.to_string();
                rendered.push('[');
                rendered.push_str(&index);
                rendered.push(']');
            }
            PathSegment::AnyElement => rendered.push_str("[]"),
        }
    }
    rendered
}
