use serde_json::{json, Value};
use std::{collections::BTreeSet, panic::catch_unwind, ptr, slice, str};
use turso_parser::{ast::*, parser::Parser};

const MAX_BYTES: usize = 1024 * 1024;

fn independent(expr: &Expr) -> bool {
    let mut pending = vec![expr];
    while let Some(expr) = pending.pop() {
        match expr {
            Expr::Literal(_) | Expr::Variable(_) => {}
            Expr::Unary(_, child) => pending.push(child),
            Expr::Binary(left, _, right) => pending.extend([left.as_ref(), right.as_ref()]),
            Expr::Parenthesized(children) => pending.extend(children.iter().map(Box::as_ref)),
            _ => return false,
        }
    }
    true
}

fn column(expr: &Expr) -> Option<Value> {
    match expr {
        Expr::Id(name) | Expr::Name(name) => {
            Some(json!({"schema": null, "table": null, "name": name.as_str()}))
        }
        Expr::Qualified(table, name) => {
            Some(json!({"schema": null, "table": table.as_str(), "name": name.as_str()}))
        }
        Expr::DoublyQualified(schema, table, name) => Some(json!({
            "schema": schema.as_str(), "table": table.as_str(), "name": name.as_str()
        })),
        _ => None,
    }
}

fn predicate(expr: Option<&Expr>) -> Value {
    let Some(expr) = expr else {
        return json!({"shape": "none", "equality_columns": []});
    };
    let mut pending = vec![expr];
    let mut columns = Vec::new();
    let mut conjunction = false;
    while let Some(expr) = pending.pop() {
        match expr {
            Expr::Parenthesized(children) if children.len() == 1 => pending.push(&children[0]),
            Expr::Binary(left, Operator::And, right) => {
                conjunction = true;
                pending.extend([right.as_ref(), left.as_ref()]);
            }
            Expr::Binary(left, Operator::Equals, right) => {
                let candidate = if independent(right) {
                    column(left)
                } else if independent(left) {
                    column(right)
                } else {
                    None
                };
                match candidate {
                    Some(candidate) => columns.push(candidate),
                    None => return json!({"shape": "other", "equality_columns": []}),
                }
            }
            _ => return json!({"shape": "other", "equality_columns": []}),
        }
    }
    let shape = if conjunction {
        "and_equalities"
    } else {
        "equality"
    };
    json!({"shape": shape, "equality_columns": columns})
}

fn target(name: &QualifiedName) -> Value {
    json!({"schema": name.db_name.as_ref().map(Name::as_str), "name": name.name.as_str()})
}

fn assignments(sets: &[Set], columns: &mut BTreeSet<String>) -> bool {
    let mut absolute = true;
    for set in sets {
        columns.extend(set.col_names.iter().map(|name| name.as_str().to_owned()));
        absolute &= independent(&set.expr);
    }
    absolute
}

fn summary(cmd: &Cmd, start: usize, end: usize) -> Value {
    let (stmt, explain) = match cmd {
        Cmd::Stmt(stmt) => (stmt, false),
        Cmd::Explain(stmt) | Cmd::ExplainQueryPlan { stmt, .. } => (stmt, true),
    };
    let mut result = json!({
        "version": 1, "start": start, "end": end, "formatted": cmd.to_string(), "kind": "other", "tables": [],
        "where": predicate(None), "columns_touched": [], "assignment_dependence": "unknown",
        "ddl": false, "explain": explain,
        "flags": {"unknown": true, "with": false, "upsert": false, "insert_select": false,
            "replace": false, "update_from": false, "conflict_clause": false,
            "returning": false, "columns_complete": false, "tables_complete": false}
    });
    let mut columns = BTreeSet::new();
    match stmt {
        Stmt::Update(update) => {
            result["kind"] = json!("update");
            result["tables"] = json!([target(&update.tbl_name)]);
            result["where"] = predicate(update.where_clause.as_deref());
            result["assignment_dependence"] = json!(if assignments(&update.sets, &mut columns) {
                "row_independent"
            } else {
                "relative_or_unknown"
            });
            result["flags"]["with"] = json!(update.with.is_some());
            result["flags"]["update_from"] = json!(update.from.is_some());
            result["flags"]["conflict_clause"] = json!(update.or_conflict.is_some());
            result["flags"]["replace"] = json!(update.or_conflict == Some(ResolveType::Replace));
            result["flags"]["returning"] = json!(!update.returning.is_empty());
            result["flags"]["columns_complete"] = json!(true);
            result["flags"]["tables_complete"] = json!(true);
            result["flags"]["unknown"] = json!(false);
        }
        Stmt::Delete {
            with,
            tbl_name,
            where_clause,
            returning,
            ..
        } => {
            result["kind"] = json!("delete");
            result["tables"] = json!([target(tbl_name)]);
            result["where"] = predicate(where_clause.as_deref());
            result["flags"]["with"] = json!(with.is_some());
            result["flags"]["returning"] = json!(!returning.is_empty());
            result["flags"]["columns_complete"] = json!(true);
            result["flags"]["tables_complete"] = json!(true);
            result["flags"]["unknown"] = json!(false);
        }
        Stmt::Insert {
            with,
            or_conflict,
            tbl_name,
            columns: names,
            body,
            returning,
        } => {
            result["kind"] = json!("insert");
            result["tables"] = json!([target(tbl_name)]);
            columns.extend(names.iter().map(|name| name.as_str().to_owned()));
            result["flags"]["with"] = json!(with.is_some());
            result["flags"]["conflict_clause"] = json!(or_conflict.is_some());
            result["flags"]["replace"] = json!(*or_conflict == Some(ResolveType::Replace));
            result["flags"]["returning"] = json!(!returning.is_empty());
            result["flags"]["columns_complete"] = json!(!names.is_empty());
            result["flags"]["tables_complete"] = json!(true);
            result["flags"]["unknown"] = json!(false);
            if let InsertBody::Select(select, upsert) = body {
                let values = matches!(&select.body.select, OneSelect::Values(_))
                    && select.body.compounds.is_empty()
                    && select.with.is_none()
                    && select.order_by.is_empty()
                    && select.limit.is_none();
                result["flags"]["insert_select"] = json!(!values);
                result["flags"]["upsert"] = json!(upsert.is_some());
                let absolute = match &select.body.select {
                    OneSelect::Values(rows) if values => {
                        rows.iter().flatten().all(|e| independent(e))
                    }
                    _ => false,
                };
                result["assignment_dependence"] = json!(if absolute && upsert.is_none() {
                    "row_independent"
                } else {
                    "relative_or_unknown"
                });
                let mut next = upsert.as_deref();
                while let Some(upsert) = next {
                    if let UpsertDo::Set { sets, .. } = &upsert.do_clause {
                        assignments(sets, &mut columns);
                    }
                    next = upsert.next.as_deref();
                }
            }
        }
        Stmt::Select(select) => {
            result["kind"] = json!("select");
            result["flags"]["with"] = json!(select.with.is_some());
        }
        Stmt::AlterTable(alter) => {
            result["kind"] = json!("ddl");
            result["ddl"] = json!(true);
            result["tables"] = json!([target(&alter.name)]);
        }
        Stmt::CreateTable { tbl_name, .. }
        | Stmt::DropTable { tbl_name, .. }
        | Stmt::CreateTrigger { tbl_name, .. } => {
            result["kind"] = json!("ddl");
            result["ddl"] = json!(true);
            result["tables"] = json!([target(tbl_name)]);
        }
        Stmt::CreateIndex { tbl_name, .. } => {
            result["kind"] = json!("ddl");
            result["ddl"] = json!(true);
            result["tables"] = json!([{"schema": null, "name": tbl_name.as_str()}]);
        }
        Stmt::CreateView { view_name, .. }
        | Stmt::DropView { view_name, .. }
        | Stmt::CreateMaterializedView { view_name, .. } => {
            result["kind"] = json!("ddl");
            result["ddl"] = json!(true);
            result["tables"] = json!([target(view_name)]);
        }
        Stmt::CreateVirtualTable(table) => {
            result["kind"] = json!("ddl");
            result["ddl"] = json!(true);
            result["tables"] = json!([target(&table.tbl_name)]);
        }
        Stmt::DropIndex { .. }
        | Stmt::DropTrigger { .. }
        | Stmt::CreateType { .. }
        | Stmt::DropType { .. }
        | Stmt::CreateDomain { .. }
        | Stmt::DropDomain { .. }
        | Stmt::CreateSequence { .. }
        | Stmt::DropSequence { .. } => {
            result["kind"] = json!("ddl");
            result["ddl"] = json!(true);
        }
        Stmt::Begin { .. }
        | Stmt::Commit { .. }
        | Stmt::Rollback { .. }
        | Stmt::Savepoint { .. }
        | Stmt::Release { .. } => result["kind"] = json!("transaction"),
        _ => {}
    }
    result["columns_touched"] = json!(columns);
    result
}

fn parse_error(error: turso_parser::error::Error) -> i32 {
    match error {
        turso_parser::error::Error::Custom(message)
        | turso_parser::error::Error::ParseError(message)
            if message == "Routing parser resource limit"
                || message.starts_with("Expression tree is too large")
                || message.starts_with("STRUCT/UNION nesting depth exceeds") =>
        {
            3
        }
        _ => 2,
    }
}

fn parse(sql: &[u8], offset: usize) -> Result<Option<Vec<u8>>, i32> {
    let mut parser = Parser::new(sql);
    let mut selected = None;
    let mut boundary = offset == 0;
    loop {
        let start = parser.offset();
        let Some(cmd) = parser.next() else { break };
        let cmd = cmd.map_err(parse_error)?;
        let end = parser.offset();
        if start == offset {
            selected = Some(serde_json::to_vec(&summary(&cmd, start, end)).map_err(|_| 6)?);
        }
        boundary |= end == offset;
    }
    if !boundary && offset != sql.len() {
        return Err(2);
    }
    Ok(selected)
}

#[no_mangle]
pub extern "C" fn ts_sql_abi_version() -> u32 {
    1
}

#[no_mangle]
pub unsafe extern "C" fn ts_sql_next(
    sql: *const u8,
    len: usize,
    offset: usize,
    out: *mut *mut u8,
    out_len: *mut usize,
) -> i32 {
    if out.is_null() || out_len.is_null() {
        return 6;
    }
    unsafe {
        out.write(ptr::null_mut());
        out_len.write(0);
    }
    let outcome = catch_unwind(|| {
        if len > MAX_BYTES {
            return Err(4);
        }
        if offset > len {
            return Err(2);
        }
        if sql.is_null() && len != 0 {
            return Err(6);
        }
        let bytes = if len == 0 {
            &[]
        } else {
            unsafe { slice::from_raw_parts(sql, len) }
        };
        str::from_utf8(bytes).map_err(|_| 5)?;
        let owned = bytes.to_vec();
        std::thread::Builder::new()
            .stack_size(16 * 1024 * 1024)
            .spawn(move || catch_unwind(|| parse(&owned, offset)).unwrap_or(Err(6)))
            .map_err(|_| 6)?
            .join()
            .unwrap_or(Err(6))
    });
    match outcome.unwrap_or(Err(6)) {
        Ok(Some(bytes)) => {
            let mut bytes = bytes.into_boxed_slice();
            unsafe {
                out_len.write(bytes.len());
                out.write(bytes.as_mut_ptr());
            }
            std::mem::forget(bytes);
            0
        }
        Ok(None) => 1,
        Err(status) => status,
    }
}

#[no_mangle]
pub unsafe extern "C" fn ts_sql_free(out: *mut u8, len: usize) {
    if !out.is_null() {
        unsafe { drop(Box::from_raw(ptr::slice_from_raw_parts_mut(out, len))) };
    }
}
