-- Smoke test: psql (18+) against TerraBase over the Postgres wire. Run through smoke.sh, which passes -v t=<table prefix>
-- and drops the tables afterwards. \bind sends the extended protocol; \parse / \bind_named use a named prepared statement.
-- A failed check prints FAIL and quits; a statement error stops psql (ON_ERROR_STOP) with exit code 3.
\set ON_ERROR_STOP 1
\set QUIET 1
\set owners :t _owners
\set items :t _items

CREATE TABLE :"owners" (id integer PRIMARY KEY, name text NOT NULL);
CREATE TABLE :"items" (sku text PRIMARY KEY, owner_id integer NOT NULL, qty integer);
\echo PASS create tables (integer and text primary keys)

INSERT INTO :"owners" (id, name) VALUES (1, 'ann'), (2, 'bob');
\if `[ :ROW_COUNT -eq 2 ] && echo true || echo false`
INSERT INTO :"items" (sku, owner_id, qty) VALUES ('a-1', 1, 3);
\echo PASS insert (simple protocol, literals)
\else
\echo FAIL insert (simple protocol, literals): ROW_COUNT=:ROW_COUNT, want 2
\quit
\endif

INSERT INTO :"items" (sku, owner_id, qty) VALUES ($1, $2, $3) \bind 'a-2' 1 5 \g
\if `[ :ROW_COUNT -eq 1 ] && echo true || echo false`
\echo PASS insert with parameters (extended protocol, unnamed statement)
\else
\echo FAIL insert with parameters: ROW_COUNT=:ROW_COUNT, want 1
\quit
\endif

INSERT INTO :"items" (sku, owner_id, qty) VALUES ($1, $2, $3) \parse ins
\bind_named ins 'b-1' 2 7 \g
\bind_named ins 'b-2' 2 9 \g
SELECT count(*) AS n FROM :"items" \gset
\if `[ :n -eq 4 ] && echo true || echo false`
\echo PASS insert with a named prepared statement, reused
\else
\echo FAIL insert with a named prepared statement: count=:n, want 4
\quit
\endif

UPDATE :"items" SET qty = qty + 1 WHERE owner_id = $1 \bind 2 \g
\if `[ :ROW_COUNT -eq 2 ] && echo true || echo false`
\echo PASS update with affected-row count
\else
\echo FAIL update: ROW_COUNT=:ROW_COUNT, want 2
\quit
\endif

DELETE FROM :"items" WHERE sku = $1 \bind 'b-2' \g
\if `[ :ROW_COUNT -eq 1 ] && echo true || echo false`
\echo PASS delete with affected-row count
\else
\echo FAIL delete: ROW_COUNT=:ROW_COUNT, want 1
\quit
\endif

SELECT o.name, i.sku, i.qty FROM :"items" i JOIN :"owners" o ON o.id = i.owner_id WHERE o.name = $1 ORDER BY i.sku \bind 'ann' \g /dev/null
\set rows :ROW_COUNT
SELECT i.qty AS q FROM :"items" i JOIN :"owners" o ON o.id = i.owner_id WHERE o.name = $1 AND i.sku = $2 \bind 'ann' 'a-2' \gset
\if `[ :rows -eq 2 ] && [ :q -eq 5 ] && echo true || echo false`
\echo PASS select with a join and a parameter
\else
\echo FAIL select with a join: rows=:rows qty=:q, want 2 rows and qty 5
\quit
\endif

BEGIN;
INSERT INTO :"items" (sku, owner_id, qty) VALUES ($1, $2, $3) \bind 'tx-1' 2 1 \g
COMMIT;
SELECT count(*) AS n FROM :"items" \gset
\if `[ :n -eq 4 ] && echo true || echo false`
\echo PASS transaction commit persists
\else
\echo FAIL transaction commit: count=:n, want 4
\quit
\endif

BEGIN;
INSERT INTO :"items" (sku, owner_id, qty) VALUES ($1, $2, $3) \bind 'tx-2' 2 1 \g
ROLLBACK;
SELECT count(*) AS n FROM :"items" \gset
\if `[ :n -eq 4 ] && echo true || echo false`
\echo PASS transaction rollback discards
\else
\echo FAIL transaction rollback: count=:n, want 4
\quit
\endif

\set ON_ERROR_STOP 0
\set VERBOSITY sqlstate
INSERT INTO :"items" (sku, owner_id, qty) VALUES ($1, $2, $3) \bind 'a-1' 1 1 \g
\set VERBOSITY default
\set ON_ERROR_STOP 1
\if `[ ":SQLSTATE" = 23505 ] && echo true || echo false`
\echo PASS unique violation is SQLSTATE 23505
\else
\echo FAIL unique violation: SQLSTATE=:SQLSTATE, want 23505
\quit
\endif

SELECT qty AS q FROM :"items" WHERE sku = $1 \parse qty
\bind_named qty 'a-1' \gset first_
\bind_named qty 'b-1' \gset second_
\if `[ :first_q -eq 3 ] && [ :second_q -eq 8 ] && echo true || echo false`
\echo PASS prepared select reused
\else
\echo FAIL prepared select reused: qty=:first_q,:second_q, want 3,8
\quit
\endif
