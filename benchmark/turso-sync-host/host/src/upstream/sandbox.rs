use turso_parser::ast::{Cmd, Stmt};
use turso_parser::parser::Parser;
use turso_sync_engine::server_proto::StreamRequest;

pub fn denied(request: &StreamRequest) -> Option<String> {
    match request {
        StreamRequest::Execute(req) => req.stmt.sql.as_deref().and_then(denied_sql),
        StreamRequest::Batch(req) => req
            .batch
            .steps
            .iter()
            .find_map(|step| step.stmt.sql.as_deref().and_then(denied_sql)),
        StreamRequest::None => None,
    }
}

fn denied_sql(sql: &str) -> Option<String> {
    for cmd in Parser::new(sql.as_bytes()) {
        let stmt = match cmd {
            Ok(Cmd::Stmt(stmt)) | Ok(Cmd::Explain(stmt)) => stmt,
            Ok(Cmd::ExplainQueryPlan { stmt, .. }) => stmt,
            Err(e) => return Some(format!("SQL could not be parsed: {e}")),
        };
        let kind = match stmt {
            Stmt::Pragma { .. } => "PRAGMA",
            Stmt::Vacuum { .. } => "VACUUM",
            Stmt::Attach { .. } => "ATTACH",
            Stmt::Detach { .. } => "DETACH",
            _ => continue,
        };
        return Some(format!("{kind} statements are not allowed"));
    }
    None
}
