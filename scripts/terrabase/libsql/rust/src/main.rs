// Proof: libsql crate (remote) against TerraBase Hrana over HTTP. See ../README.md.
use std::hash::{BuildHasher, RandomState};

use libsql::{Builder, Connection, named_params, params};

type Res<T> = Result<T, Box<dyn std::error::Error>>;

#[tokio::main]
async fn main() {
    let (url, token_file) = match (std::env::var("LIBSQL_URL"), std::env::var("LIBSQL_TOKEN_FILE")) {
        (Ok(u), Ok(f)) => (u, f),
        _ => {
            eprintln!("set LIBSQL_URL and LIBSQL_TOKEN_FILE");
            std::process::exit(2);
        }
    };
    let token = match std::fs::read_to_string(&token_file) {
        Ok(t) => t.trim().to_owned(),
        Err(e) => {
            eprintln!("read token file: {e}");
            std::process::exit(2);
        }
    };
    let db = Builder::new_remote(url, token).build().await.expect("open database");
    let conn = db.connect().expect("connect");
    let t = format!("proof_rust_{:08x}", RandomState::new().hash_one(0u8) as u32);
    let (owners, items) = (format!("{t}_owners"), format!("{t}_items"));

    let mut ok = match proof(&conn, &owners, &items).await {
        Ok(()) => true,
        Err(e) => {
            println!("error: {e}");
            false
        }
    };
    let drop = format!("DROP TABLE IF EXISTS {items}; DROP TABLE IF EXISTS {owners};");
    match conn.execute_batch(&drop).await {
        Ok(_) => println!("drop tables"),
        Err(e) => {
            println!("drop failed: {e}");
            ok = false;
        }
    }
    println!("{}", if ok { "PASS" } else { "FAIL" });
    std::process::exit(if ok { 0 } else { 1 });
}

fn check(cond: bool, what: &str) -> Res<()> {
    if cond { Ok(()) } else { Err(format!("check failed: {what}").into()) }
}

async fn count(conn: &Connection, items: &str) -> Res<i64> {
    let mut rows = conn.query(&format!("SELECT count(*) FROM {items}"), ()).await?;
    Ok(rows.next().await?.ok_or("no row")?.get(0)?)
}

async fn proof(conn: &Connection, owners: &str, items: &str) -> Res<()> {
    println!("create tables {owners}, {items}");
    conn.execute(&format!("CREATE TABLE {owners} (id INTEGER PRIMARY KEY, name TEXT NOT NULL)"), ()).await?;
    conn.execute(&format!("CREATE TABLE {items} (sku TEXT PRIMARY KEY, owner_id INTEGER NOT NULL, qty INTEGER)"), ()).await?;

    let n = conn.execute(&format!("INSERT INTO {owners} (id, name) VALUES (?1, ?2)"), params![1, "ann"]).await?;
    conn.execute(&format!("INSERT INTO {owners} (id, name) VALUES (:id, :name)"), named_params! {":id": 2, ":name": "bob"}).await?;
    println!("insert with parameters: rows affected={n}");
    check(n == 1, "insert affected 1 row")?;

    // The crate's batch API takes SQL text only; it runs as one transaction.
    conn.execute_transactional_batch(&format!(
        "INSERT INTO {items} (sku, owner_id, qty) VALUES ('a-1', 1, 3); \
         INSERT INTO {items} (sku, owner_id, qty) VALUES ('a-2', 1, 5); \
         INSERT INTO {items} (sku, owner_id, qty) VALUES ('b-1', 2, 7);"
    ))
    .await?;
    println!("batch (transactional): 3 statements");

    let mut rows = conn
        .query(&format!("SELECT o.name, i.sku, i.qty FROM {items} i JOIN {owners} o ON o.id = i.owner_id ORDER BY i.sku"), ())
        .await?;
    let mut got = Vec::new();
    while let Some(r) = rows.next().await? {
        got.push(format!("{}/{}/{}", r.get::<String>(0)?, r.get::<String>(1)?, r.get::<i64>(2)?));
    }
    println!("select with join: {}", got.join(", "));
    check(got.len() == 3 && got[2].starts_with("bob/"), "join returned 3 rows, last owned by bob")?;

    for (sku, commit) in [("tx-1", true), ("tx-2", false)] {
        let tx = conn.transaction().await?;
        tx.execute(&format!("INSERT INTO {items} (sku, owner_id, qty) VALUES (?1, 2, 1)"), params![sku]).await?;
        let mut seen = tx.query(&format!("SELECT count(*) FROM {items} WHERE sku = ?1"), params![sku]).await?;
        check(seen.next().await?.ok_or("no row")?.get::<i64>(0)? == 1, "transaction sees its own insert")?;
        drop(seen);
        if commit { tx.commit().await? } else { tx.rollback().await? }
        let c = count(conn, items).await?;
        println!("transaction {}: count={c}", if commit { "commit" } else { "rollback" });
        check(c == 4, "count is 4 after commit and after rollback")?;
    }
    Ok(())
}
