//! A deliberately small, bounded CEL-compatible row predicate.
//!
//! This is not a general CEL interpreter.  The accepted subset consists of
//! comparisons between a document field (`resource.foo`, `resource.data.foo`,
//! or `doc.foo`) and a scalar principal claim, combined with `&&`, `||`, `!`,
//! and parentheses.  Restricting the language here makes publication and
//! evaluation finite and gives the query planner an exact index dependency.

use std::collections::{BTreeMap, BTreeSet};

use super::{AttributeValue, AuthorizationError, PrincipalClaims, MAX_ACCESS_RULE_BYTES,
    MAX_ACCESS_RULE_NODES};

const MAX_TOKENS: usize = MAX_ACCESS_RULE_NODES * 2;

/// Entry point for compiling the supported CEL row-rule subset.
pub struct AccessRule;

impl AccessRule {
    /// Compiles an expression and verifies every document field has an index.
    pub fn try_compile(
        expression: &str,
        indexed_fields: &BTreeSet<String>,
    ) -> Result<CompiledAccessRule, AuthorizationError> {
        CompiledAccessRule::try_compile(expression, indexed_fields)
    }
}

/// A validated, immutable row predicate with a deterministic AST and index
/// dependency set.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CompiledAccessRule {
    expression: String,
    root: Expr,
    fields: BTreeSet<String>,
    nodes: usize,
}

impl CompiledAccessRule {
    /// Parses and validates one bounded expression.
    pub fn try_compile(
        expression: &str,
        indexed_fields: &BTreeSet<String>,
    ) -> Result<Self, AuthorizationError> {
        if expression.is_empty()
            || expression.len() > MAX_ACCESS_RULE_BYTES
            || expression.chars().any(char::is_control)
        {
            return Err(AuthorizationError::InvalidAccessRule);
        }
        let tokens = lex(expression)?;
        let mut parser = Parser { tokens: &tokens, position: 0, fields: BTreeSet::new(), nodes: 0 };
        let root = parser.parse_expression()?;
        if !matches!(parser.peek(), Token::End) || parser.nodes > MAX_ACCESS_RULE_NODES {
            return Err(AuthorizationError::InvalidAccessRule);
        }
        if let Some(field) = parser.fields.iter().find(|field| !indexed_fields.contains(*field)) {
            return Err(AuthorizationError::UnindexedAccessRuleField(field.clone()));
        }
        let expression = root.canonical();
        Ok(Self { expression, root, fields: parser.fields, nodes: parser.nodes })
    }

    /// Returns the normalized, stable expression.
    #[must_use]
    pub fn expression(&self) -> &str {
        &self.expression
    }

    /// Returns exact document field dependencies used for index planning.
    #[must_use]
    pub fn fields(&self) -> &BTreeSet<String> {
        &self.fields
    }

    /// Returns the fixed AST node count used by the bounded evaluator.
    #[must_use]
    pub const fn nodes(&self) -> usize {
        self.nodes
    }

    /// Evaluates this predicate against one already-extracted row and claims.
    /// Missing fields/claims and unlike scalar types always evaluate false.
    #[must_use]
    pub fn evaluate(
        &self,
        row: &BTreeMap<String, AttributeValue>,
        claims: &PrincipalClaims,
    ) -> bool {
        let mut budget = self.nodes;
        self.root.evaluate(row, claims, &mut budget)
    }

    pub(crate) fn canonical(&self) -> String {
        self.expression.clone()
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
enum Expr {
    Literal(AttributeValue),
    Row(String),
    Claim(String),
    Compare(Box<Expr>, Compare, Box<Expr>),
    And(Box<Expr>, Box<Expr>),
    Or(Box<Expr>, Box<Expr>),
    Not(Box<Expr>),
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum Compare {
    Eq,
    Ne,
    Lt,
    Le,
    Gt,
    Ge,
}

impl Expr {
    fn canonical(&self) -> String {
        match self {
            Self::Literal(AttributeValue::Boolean(value)) => value.to_string(),
            Self::Literal(AttributeValue::Integer(value)) => value.to_string(),
            Self::Literal(AttributeValue::String(value)) => format!("\"{}\"", escape(value)),
            Self::Row(field) => format!("resource.{field}"),
            Self::Claim(claim) => format!("request.auth.claims.{claim}"),
            Self::Compare(left, op, right) => {
                format!("({}{}{})", left.canonical(), op.canonical(), right.canonical())
            }
            Self::And(left, right) => format!("({}&&{})", left.canonical(), right.canonical()),
            Self::Or(left, right) => format!("({}||{})", left.canonical(), right.canonical()),
            Self::Not(value) => format!("!{}", value.canonical()),
        }
    }

    fn resolve<'a>(
        &'a self,
        row: &'a BTreeMap<String, AttributeValue>,
        claims: &'a PrincipalClaims,
    ) -> Option<&'a AttributeValue> {
        match self {
            Self::Row(field) => row.get(field),
            Self::Claim(claim) => claims.get(claim),
            Self::Literal(value) => Some(value),
            _ => None,
        }
    }

    fn evaluate(
        &self,
        row: &BTreeMap<String, AttributeValue>,
        claims: &PrincipalClaims,
        budget: &mut usize,
    ) -> bool {
        if *budget == 0 {
            return false;
        }
        *budget -= 1;
        match self {
            Self::And(left, right) => {
                left.evaluate(row, claims, budget) && right.evaluate(row, claims, budget)
            }
            Self::Or(left, right) => {
                left.evaluate(row, claims, budget) || right.evaluate(row, claims, budget)
            }
            Self::Not(value) => !value.evaluate(row, claims, budget),
            Self::Compare(left, op, right) => {
                let (Some(left), Some(right)) = (left.resolve(row, claims), right.resolve(row, claims)) else {
                    return false;
                };
                compare(left, right, *op)
            }
            Self::Literal(_) | Self::Row(_) | Self::Claim(_) => false,
        }
    }
}

impl Compare {
    const fn canonical(self) -> &'static str {
        match self {
            Self::Eq => "==",
            Self::Ne => "!=",
            Self::Lt => "<",
            Self::Le => "<=",
            Self::Gt => ">",
            Self::Ge => ">=",
        }
    }
}

fn compare(left: &AttributeValue, right: &AttributeValue, op: Compare) -> bool {
    let ordering = match (left, right) {
        (AttributeValue::Boolean(left), AttributeValue::Boolean(right)) => left.cmp(right),
        (AttributeValue::Integer(left), AttributeValue::Integer(right)) => left.cmp(right),
        (AttributeValue::String(left), AttributeValue::String(right)) => left.cmp(right),
        _ => return matches!(op, Compare::Ne),
    };
    match op {
        Compare::Eq => ordering.is_eq(),
        Compare::Ne => ordering.is_ne(),
        Compare::Lt => ordering.is_lt(),
        Compare::Le => !ordering.is_gt(),
        Compare::Gt => ordering.is_gt(),
        Compare::Ge => !ordering.is_lt(),
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
enum Token {
    Ident(String),
    String(String),
    Integer(i64),
    Bool(bool),
    Eq,
    Ne,
    Lt,
    Le,
    Gt,
    Ge,
    And,
    Or,
    Not,
    Left,
    Right,
    End,
}

fn lex(input: &str) -> Result<Vec<Token>, AuthorizationError> {
    let bytes = input.as_bytes();
    let mut tokens = Vec::new();
    let mut offset = 0;
    while offset < bytes.len() {
        if bytes[offset].is_ascii_whitespace() {
            offset += 1;
            continue;
        }
        let byte = bytes[offset];
        let token = match byte {
            b'(' => { offset += 1; Token::Left }
            b')' => { offset += 1; Token::Right }
            b'!' if bytes.get(offset + 1) == Some(&b'=') => { offset += 2; Token::Ne }
            b'!' => { offset += 1; Token::Not }
            b'=' if bytes.get(offset + 1) == Some(&b'=') => { offset += 2; Token::Eq }
            b'<' if bytes.get(offset + 1) == Some(&b'=') => { offset += 2; Token::Le }
            b'>' if bytes.get(offset + 1) == Some(&b'=') => { offset += 2; Token::Ge }
            b'<' => { offset += 1; Token::Lt }
            b'>' => { offset += 1; Token::Gt }
            b'&' if bytes.get(offset + 1) == Some(&b'&') => { offset += 2; Token::And }
            b'|' if bytes.get(offset + 1) == Some(&b'|') => { offset += 2; Token::Or }
            b'"' | b'\'' => {
                let quote = byte;
                offset += 1;
                let mut value = String::new();
                let mut closed = false;
                while offset < bytes.len() {
                    let current = bytes[offset];
                    offset += 1;
                    if current == quote { closed = true; break; }
                    if current == b'\\' {
                        let escaped = *bytes.get(offset).ok_or(AuthorizationError::InvalidAccessRule)?;
                        offset += 1;
                        value.push(match escaped { b'\\' => '\\', b'"' => '"', b'\'' => '\'', b'n' => '\n', b'r' => '\r', b't' => '\t', _ => return Err(AuthorizationError::InvalidAccessRule) });
                    } else {
                        value.push(char::from(current));
                    }
                }
                if !closed || value.is_empty() { return Err(AuthorizationError::InvalidAccessRule); }
                Token::String(value)
            }
            b'-' | b'0'..=b'9' => {
                let start = offset;
                offset += usize::from(byte == b'-');
                while bytes.get(offset).is_some_and(u8::is_ascii_digit) { offset += 1; }
                let value = input.get(start..offset).ok_or(AuthorizationError::InvalidAccessRule)?.parse().map_err(|_| AuthorizationError::InvalidAccessRule)?;
                Token::Integer(value)
            }
            _ if byte.is_ascii_alphabetic() || byte == b'_' => {
                let start = offset;
                offset += 1;
                while bytes.get(offset).is_some_and(|value| value.is_ascii_alphanumeric() || *value == b'_' || *value == b'.') { offset += 1; }
                let value = input.get(start..offset).ok_or(AuthorizationError::InvalidAccessRule)?;
                match value { "true" => Token::Bool(true), "false" => Token::Bool(false), _ => Token::Ident(value.to_owned()) }
            }
            _ => return Err(AuthorizationError::InvalidAccessRule),
        };
        tokens.push(token);
        if tokens.len() > MAX_TOKENS { return Err(AuthorizationError::AccessRuleTooLarge); }
    }
    tokens.push(Token::End);
    Ok(tokens)
}

struct Parser<'a> {
    tokens: &'a [Token],
    position: usize,
    fields: BTreeSet<String>,
    nodes: usize,
}

impl Parser<'_> {
    fn peek(&self) -> &Token { &self.tokens[self.position] }
    fn take(&mut self) -> Token { let token = self.tokens[self.position].clone(); self.position += 1; token }
    fn node(&mut self) -> Result<(), AuthorizationError> { self.nodes += 1; if self.nodes > MAX_ACCESS_RULE_NODES { Err(AuthorizationError::AccessRuleTooLarge) } else { Ok(()) } }
    fn parse_expression(&mut self) -> Result<Expr, AuthorizationError> { self.parse_or() }
    fn parse_or(&mut self) -> Result<Expr, AuthorizationError> {
        let mut value = self.parse_and()?;
        while matches!(self.peek(), Token::Or) { self.take(); value = Expr::Or(Box::new(value), Box::new(self.parse_and()?)); self.node()?; }
        Ok(value)
    }
    fn parse_and(&mut self) -> Result<Expr, AuthorizationError> {
        let mut value = self.parse_unary()?;
        while matches!(self.peek(), Token::And) { self.take(); value = Expr::And(Box::new(value), Box::new(self.parse_unary()?)); self.node()?; }
        Ok(value)
    }
    fn parse_unary(&mut self) -> Result<Expr, AuthorizationError> {
        if matches!(self.peek(), Token::Not) { self.take(); let value = Expr::Not(Box::new(self.parse_unary()?)); self.node()?; return Ok(value); }
        if matches!(self.peek(), Token::Left) { self.take(); let value = self.parse_expression()?; if !matches!(self.take(), Token::Right) { return Err(AuthorizationError::InvalidAccessRule); } return Ok(value); }
        self.parse_comparison()
    }
    fn parse_comparison(&mut self) -> Result<Expr, AuthorizationError> {
        let left = self.parse_operand()?;
        let op = match self.take() { Token::Eq => Compare::Eq, Token::Ne => Compare::Ne, Token::Lt => Compare::Lt, Token::Le => Compare::Le, Token::Gt => Compare::Gt, Token::Ge => Compare::Ge, _ => return Err(AuthorizationError::InvalidAccessRule) };
        let right = self.parse_operand()?;
        self.node()?;
        Ok(Expr::Compare(Box::new(left), op, Box::new(right)))
    }
    fn parse_operand(&mut self) -> Result<Expr, AuthorizationError> {
        let token = self.take();
        self.node()?;
        match token {
            Token::Integer(value) => Ok(Expr::Literal(AttributeValue::Integer(value))),
            Token::String(value) => Ok(Expr::Literal(AttributeValue::String(value))),
            Token::Bool(value) => Ok(Expr::Literal(AttributeValue::Boolean(value))),
            Token::Ident(value) => {
                let Some(field) = value.strip_prefix("resource.data.").or_else(|| value.strip_prefix("resource.")).or_else(|| value.strip_prefix("doc.")) else {
                    let claim = value.strip_prefix("request.auth.claims.").or_else(|| value.strip_prefix("request.auth.token.")).or_else(|| value.strip_prefix("claims.")).or_else(|| value.strip_prefix("auth.")).ok_or(AuthorizationError::InvalidAccessRule)?;
                    if !valid_name(claim) { return Err(AuthorizationError::InvalidAccessRule); }
                    return Ok(Expr::Claim(claim.to_owned()));
                };
                if !valid_name(field) { return Err(AuthorizationError::InvalidAccessRule); }
                self.fields.insert(field.to_owned());
                Ok(Expr::Row(field.to_owned()))
            }
            _ => Err(AuthorizationError::InvalidAccessRule),
        }
    }
}

fn valid_name(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 255
        && value.split('.').all(|part| {
            !part.is_empty()
                && !matches!(part, "matches" | "startsWith" | "endsWith" | "contains" | "size" | "exists")
                && part.chars().all(|char| char.is_ascii_alphanumeric() || char == '_')
        })
}

fn escape(value: &str) -> String {
    value.replace('\\', "\\\\").replace('"', "\\\"").replace('\n', "\\n").replace('\r', "\\r").replace('\t', "\\t")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn claims() -> PrincipalClaims {
        PrincipalClaims::try_new([("tenant", AttributeValue::String("acme".to_owned()))])
            .expect("claims")
    }

    #[test]
    fn row_rule_is_bounded_indexed_and_deterministic() {
        let indexes = BTreeSet::from(["owner".to_owned(), "active".to_owned()]);
        let rule = AccessRule::try_compile(
            " resource.data.owner == request.auth.claims.tenant && doc.active == true ",
            &indexes,
        )
        .expect("rule");
        assert_eq!(rule.fields(), &BTreeSet::from(["owner".to_owned(), "active".to_owned()]));
        assert_eq!(rule.expression(), "((resource.owner==request.auth.claims.tenant)&&(resource.active==true))");
        let row = BTreeMap::from([
            ("owner".to_owned(), AttributeValue::String("acme".to_owned())),
            ("active".to_owned(), AttributeValue::Boolean(true)),
        ]);
        assert!(rule.evaluate(&row, &claims()));
    }

    #[test]
    fn missing_or_cross_tenant_values_deny() {
        let rule = AccessRule::try_compile(
            "resource.owner == claims.tenant",
            &BTreeSet::from(["owner".to_owned()]),
        )
        .expect("rule");
        let row = BTreeMap::from([(
            "owner".to_owned(),
            AttributeValue::String("other".to_owned()),
        )]);
        assert!(!rule.evaluate(&row, &claims()));
        assert!(!rule.evaluate(&BTreeMap::new(), &claims()));
    }

    #[test]
    fn hostile_cel_is_rejected_or_requires_an_index() {
        assert_eq!(
            AccessRule::try_compile("resource.owner == claims.tenant", &BTreeSet::new()),
            Err(AuthorizationError::UnindexedAccessRuleField("owner".to_owned()))
        );
        assert_eq!(
            AccessRule::try_compile("resource.owner.matches('.*')", &BTreeSet::from(["owner".to_owned()])),
            Err(AuthorizationError::InvalidAccessRule)
        );
        assert_eq!(
            AccessRule::try_compile("true", &BTreeSet::new()),
            Err(AuthorizationError::InvalidAccessRule)
        );
    }
}
