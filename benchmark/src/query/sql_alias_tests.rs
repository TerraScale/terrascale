//! Supplementary collector coverage for sqlparser 0.62 multi-column aliases.

use super::*;
use sqlparser::ast::{
    BinaryOperator, Expr, Ident, OrderByExpr, OrderByKind, SelectItem, SetExpr, Statement,
};

#[test]
fn multi_alias_projection_retains_input_fields_but_order_by_skips_aliases() {
    let mut parsed = parse("SELECT amount FROM records ORDER BY amount", 16 * 1024).unwrap();
    let Statement::Query(query) = &mut parsed.statement else { panic!("expected query") };
    let SetExpr::Select(select) = query.body.as_mut() else { panic!("expected select") };
    select.projection[0] = SelectItem::ExprWithAliases {
        expr: Expr::BinaryOp {
            left: Box::new(Expr::Identifier(Ident::new("total"))),
            op: BinaryOperator::Plus,
            right: Box::new(Expr::Identifier(Ident::new("amount"))),
        },
        aliases: vec![Ident::new("total"), Ident::new("output")],
    };
    let order_by = query.order_by.as_mut().unwrap();
    let OrderByKind::Expressions(expressions) = &mut order_by.kind else {
        panic!("expected expression ordering")
    };
    expressions.clear();
    expressions.push(OrderByExpr::from(Ident::new("total")));
    expressions.push(OrderByExpr::from(Ident::new("output")));
    assert_eq!(direct_single_base_referenced_fields(&parsed), ["amount", "total"]);
}
