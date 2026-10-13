#!/usr/bin/env bash
# Regenerates the import fixtures (tests/TerraBase.Fast/Import/Fixtures) with genuine tools: pg_dump 17 against a
# throwaway local cluster, and sqlite3. Synthetic data only. Needs nix (or pg_dump/initdb/postgres/sqlite3 on PATH).
#   nix shell nixpkgs#postgresql_17 nixpkgs#sqlite --command scripts/terrabase/import-fixtures.sh
set -euo pipefail
out="$(cd "$(dirname "$0")/../.." && pwd)/tests/TerraBase.Fast/Import/Fixtures"
work="$(mktemp -d)"
trap 'pg_ctl -D "$work/pg" -m immediate stop >/dev/null 2>&1 || true; rm -rf "$work"' EXIT

initdb -D "$work/pg" -U fixture --auth=trust --encoding=UTF8 --locale=C >/dev/null
pg_ctl -D "$work/pg" -o "-k $work -c listen_addresses='' -c fsync=off" -l "$work/pg.log" -w start >/dev/null
psql=(psql -h "$work" -U fixture -d postgres -v ON_ERROR_STOP=1 -q)

"${psql[@]}" <<'SQL'
CREATE TYPE public.mood AS ENUM ('sad', 'ok', 'happy');
CREATE SCHEMA sales;
CREATE TABLE public.orgs (id bigint PRIMARY KEY, name text NOT NULL);
CREATE TABLE public.users (
    id uuid PRIMARY KEY,
    org_id bigint NOT NULL REFERENCES public.orgs(id),
    email character varying(200) NOT NULL UNIQUE,
    "Display Name" text,
    active boolean NOT NULL DEFAULT true,
    balance numeric(14,4),
    score double precision,
    ratio real,
    small smallint,
    mood public.mood,
    born date,
    seen_at timestamp without time zone,
    created_at timestamp with time zone NOT NULL,
    profile jsonb,
    settings json,
    avatar bytea,
    wake time without time zone,
    span interval,
    ip inet,
    flags bit varying(8),
    CONSTRAINT users_score_check CHECK (score >= 0)
);
CREATE TABLE sales."Order Items" (
    order_id integer NOT NULL,
    line integer NOT NULL,
    sku text NOT NULL,
    "unit.price" numeric NOT NULL,
    PRIMARY KEY (order_id, line)
);
CREATE TABLE public.events (at timestamp with time zone NOT NULL, kind text NOT NULL, payload jsonb);
CREATE TABLE public.tagged (id integer PRIMARY KEY, tags text[]);
CREATE TABLE public.measurements (id integer NOT NULL, taken date NOT NULL, value numeric, PRIMARY KEY (id, taken)) PARTITION BY RANGE (taken);
CREATE TABLE public.measurements_2025 PARTITION OF public.measurements FOR VALUES FROM ('2025-01-01') TO ('2026-01-01');
CREATE TABLE public.measurements_2026 PARTITION OF public.measurements FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');
CREATE INDEX users_org_idx ON public.users (org_id);
CREATE VIEW public.active_users AS SELECT id, email FROM public.users WHERE active;
CREATE FUNCTION public.touch() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN NEW.seen_at := now(); RETURN NEW; END; $$;
CREATE TRIGGER users_touch BEFORE UPDATE ON public.users FOR EACH ROW EXECUTE FUNCTION public.touch();
COMMENT ON TABLE public.users IS 'synthetic fixture users; it''s fine';

INSERT INTO public.orgs VALUES (1, 'Acme'), (2, E'Tab\tand\nnewline'), (9007199254740993, 'Big id');
INSERT INTO public.users VALUES
  ('5f0c8e9a-6a43-4c3e-9a7d-2d1f0c5e8b11', 1, 'ada@example.com', 'Ada Lovelace', true, 1234567890.1234, 0.1, 1.5, -32768,
   'happy', '1815-12-10', '2026-10-12 08:30:00.123456', '2026-10-12 08:30:00.123456+00',
   '{"a": [1, 2, {"b": null}], "ü": "ß"}', '{"z":1, "a":2}', '\x00ff10', '23:59:59.999999', '1 year 2 mons 3 days 04:05:06', '192.168.0.1/24', B'101'),
  ('0b8e7f6d-1c2a-4b3e-8f9a-0a1b2c3d4e5f', 2, 'grace@example.com', NULL, false, NULL, 1e300, NULL, NULL,
   NULL, NULL, NULL, '1999-12-31 23:59:59-03', NULL, NULL, NULL, NULL, NULL, NULL, NULL),
  ('a1b2c3d4-e5f6-4711-8899-aabbccddeeff', 1, 'back\slash@example.com', E'Quote " and \\ backslash', true, 0.0001, 12345.678, -0.25, 7,
   'sad', '2000-02-29', '2000-02-29 00:00:00', '2000-02-29 12:00:00+05:30',
   '[]', 'null', '\x', '00:00:00', '-1 days', '::1', B'');
INSERT INTO sales."Order Items" VALUES (1, 1, 'SKU-1', 9.99), (1, 2, 'SKU:2%', 0.000000000000000000000000001), (2, 1, 'SKU-3', 123456789012345678901234567890.5);
INSERT INTO public.events VALUES ('2026-10-12 00:00:00+00', 'login', '{"ok": true}'), ('2026-10-12 00:00:01+00', 'logout', NULL);
INSERT INTO public.tagged VALUES (1, ARRAY['a', 'b']);
INSERT INTO public.measurements VALUES (1, '2025-06-01', 1.5), (2, '2026-06-01', 2.5), (3, '2026-07-01', NULL);
SQL

pg_dump -h "$work" -U fixture --format=plain --no-owner --no-privileges --encoding=UTF8 -f "$out/pg17-shop.sql" postgres
# A dump with values that have no JSON form, for the preview's invalid_value issues.
"${psql[@]}" <<'SQL'
CREATE DATABASE edge;
\c edge
CREATE TABLE public.readings (id integer PRIMARY KEY, at timestamp with time zone, day date, value double precision);
INSERT INTO public.readings VALUES (1, '2026-01-01 00:00:00+00', '2026-01-01', 1), (2, 'infinity', '2026-01-02', 2),
  (3, '2026-01-03 00:00:00+00', '0044-03-15 BC', 3), (4, '2026-01-04 00:00:00+00', '2026-01-04', 'NaN'),
  (5, '2026-01-05 00:00:00+00', '2026-01-05', 1e308);
SQL
pg_dump -h "$work" -U fixture --format=plain --no-owner --no-privileges --encoding=UTF8 -f "$out/pg17-edge.sql" edge
pg_dump -h "$work" -U fixture --format=plain --no-owner --no-privileges --encoding=UTF8 --inserts -f "$out/pg17-inserts.sql" edge
pg_dump -h "$work" -U fixture --format=custom -f "$out/pg17-custom.dump" edge

# SQLite: a consolidated rollback-mode file, and a WAL-mode file (.backup keeps the WAL flag).
rm -f "$out"/sqlite-*.db
sqlite3 "$work/live.db" <<'SQL'
PRAGMA journal_mode=WAL;
CREATE TABLE notes (id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL, body TEXT, score REAL, data BLOB, misc);
INSERT INTO notes (title, body, score, data, misc) VALUES
  ('first', 'hello', 1.5, x'00ff', 1),
  ('second', NULL, NULL, NULL, 'text'),
  ('unicode', 'ü ß 漢字 🎉', -0.0, x'', NULL);
CREATE TABLE kv (k TEXT PRIMARY KEY, v) WITHOUT ROWID;
INSERT INTO kv VALUES ('a', 1), ('b:c', 2.25), ('d%e', NULL);
CREATE TABLE plain (x, y);
INSERT INTO plain VALUES (1, 2), (3, 4);
ALTER TABLE plain ADD COLUMN z TEXT DEFAULT 'zed';
INSERT INTO plain VALUES (5, 6, 'set');
CREATE TABLE "Weird.Name" ("Col One" INTEGER PRIMARY KEY, "two" TEXT);
INSERT INTO "Weird.Name" VALUES (10, 'ten');
CREATE TABLE wr (a INTEGER, b TEXT, c TEXT, PRIMARY KEY (c, a)) WITHOUT ROWID;
INSERT INTO wr VALUES (1, 'bee', 'x'), (2, 'bee2', 'x'), (1, 'bee3', 'y');
CREATE TABLE g (a INTEGER PRIMARY KEY, b INTEGER GENERATED ALWAYS AS (a * 2) VIRTUAL, c INTEGER AS (a + 1) STORED, d TEXT);
INSERT INTO g (a, d) VALUES (1, 'one'), (2, 'two');
CREATE VIRTUAL TABLE notes_fts USING fts5(title, body);
INSERT INTO notes_fts SELECT title, body FROM notes;
CREATE VIEW titles AS SELECT title FROM notes;
CREATE INDEX notes_title ON notes(title);
CREATE TRIGGER notes_ai AFTER INSERT ON notes BEGIN INSERT INTO notes_fts(title, body) VALUES (new.title, new.body); END;
ANALYZE;
SQL
# Many rows over several b-tree levels with overflow pages.
sqlite3 "$work/live.db" "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 1200) INSERT INTO notes (title, body, score) SELECT 'row ' || i, CASE WHEN i % 300 = 0 THEN printf('%.*c', 9000, 'x') ELSE 'b' || i END, i / 4.0 FROM n;"
sqlite3 "$work/live.db" "VACUUM INTO '$out/sqlite-notes.db'"
sqlite3 "$work/live.db" ".backup '$out/sqlite-wal.db'"
# Crafted SQLite files for the reader's refusals and edge cases (sqlite3 itself writes them).
rm -f "$out"/sqlite-crafted-*.db
sqlite3 "$out/sqlite-crafted-neighbors.db" <<'SQL'
CREATE VIRTUAL TABLE notes USING fts5(body);
INSERT INTO notes VALUES ('indexed text');
CREATE TABLE notes_tags (id INTEGER PRIMARY KEY, tag TEXT);
INSERT INTO notes_tags VALUES (1, 'kept');
CREATE TABLE typed (id INTEGER PRIMARY KEY, a TEXT);
INSERT INTO typed VALUES (1, 'old');
ALTER TABLE typed ADD COLUMN n INTEGER DEFAULT '5';
ALTER TABLE typed ADD COLUMN h INTEGER DEFAULT 0x10;
ALTER TABLE typed ADD COLUMN r REAL DEFAULT 2;
INSERT INTO typed VALUES (2, 'new', 7, 8, 9.5);
SQL
sqlite3 "$out/sqlite-crafted-shared-root.db" <<'SQL'
CREATE TABLE a (id INTEGER PRIMARY KEY, v TEXT);
CREATE TABLE b (id INTEGER PRIMARY KEY, v TEXT);
INSERT INTO a VALUES (1, 'x');
.dbconfig defensive off
PRAGMA writable_schema = ON;
UPDATE sqlite_schema SET rootpage = (SELECT rootpage FROM sqlite_schema WHERE name = 'a') WHERE name = 'b';
SQL
sqlite3 "$out/sqlite-crafted-duplicate.db" <<'SQL'
CREATE TABLE a (id INTEGER PRIMARY KEY);
CREATE TABLE b (id INTEGER PRIMARY KEY);
.dbconfig defensive off
PRAGMA writable_schema = ON;
UPDATE sqlite_schema SET name = 'a', tbl_name = 'a' WHERE name = 'b';
SQL
pg_ctl -D "$work/pg" -m fast stop >/dev/null
echo "pg_dump: $(pg_dump --version); sqlite3: $(sqlite3 --version | cut -d' ' -f1)" > "$out/VERSIONS"
