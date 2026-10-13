#!/usr/bin/env python3
"""Parallel, isolated CLI checks using synthetic inputs; no services or dependencies."""

from concurrent.futures import ThreadPoolExecutor, as_completed
import base64
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile


HERE = Path(__file__).resolve().parent
CLI = HERE / "mysql_import.py"
PREFIX = "SET NAMES utf8mb4; SET SQL_MODE='NO_AUTO_VALUE_ON_ZERO'; SET TIME_ZONE='+00:00';\n"
SCHEMA = "CREATE TABLE `t` (`id` bigint NOT NULL, `v` {type}, PRIMARY KEY (`id`)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;\n"


def dump(value, type="text", prefix=PREFIX):
    return prefix + SCHEMA.format(type=type) + "INSERT INTO `t` (`id`,`v`) VALUES " + value + ";\n"


def invoke(source, destination, action="convert", flavor="mysql-8.4"):
    return subprocess.run(
        [sys.executable, "-I", "-B", str(CLI), action, str(source), str(destination), "--source-version", flavor],
        capture_output=True, text=True, timeout=60, check=False,
    )


def exercise(data, expected_code=None, check=None, flavor="mysql-8.4"):
    with tempfile.TemporaryDirectory(prefix=".test-", dir=HERE) as directory:
        root = Path(directory)
        source = root / "synthetic.sql"
        original = data.encode("utf-8") if isinstance(data, str) else data
        source.write_bytes(original)
        source.chmod(0o400)
        before = source.stat()
        destination = root / "converted"
        result = invoke(source, destination, flavor=flavor)
        assert source.read_bytes() == original
        after = source.stat()
        assert (before.st_size, before.st_mtime_ns, before.st_mode) == (after.st_size, after.st_mtime_ns, after.st_mode)
        assert not list(root.glob(".mysql-*")), "scratch files retained"
        if expected_code:
            assert result.returncode == 1, "unsupported input accepted"
            assert not destination.exists(), "partial output published"
            assert f"[{expected_code}]" in result.stderr, result.stderr
            assert result.stdout == ""
        else:
            assert result.returncode == 0, result.stderr
            report = json.loads((destination / "report.json").read_text(encoding="utf-8"))
            artifact = (destination / "import.sql").read_bytes()
            assert report["sourceSha256"] == hashlib.sha256(original).hexdigest()
            assert report["outputSha256"] == hashlib.sha256(artifact).hexdigest()
            assert report["outputBytes"] == len(artifact)
            assert report["sourceBytes"] == len(original)
            assert report["sourceVersionVerified"] is False
            assert destination.stat().st_mode & 0o077 == 0
            assert (destination / "import.sql").stat().st_mode & 0o077 == 0
            assert (destination / "report.json").stat().st_mode & 0o077 == 0
            if check:
                check(root, source, destination, artifact, report)


def copy_fields(line):
    # Independent decoder for PostgreSQL COPY text, including octal escapes.
    fields = []
    for field in line.split("\t"):
        if field == r"\N":
            fields.append(None)
            continue
        output, index = [], 0
        escapes = {"b": "\b", "f": "\f", "n": "\n", "r": "\r", "t": "\t", "v": "\v"}
        while index < len(field):
            char = field[index]
            index += 1
            if char == "\\":
                char = field[index]
                index += 1
                if char in "01234567":
                    octal = char
                    while index < len(field) and len(octal) < 3 and field[index] in "01234567":
                        octal += field[index]
                        index += 1
                    char = chr(int(octal, 8))
                else:
                    char = escapes.get(char, char)
            output.append(char)
        fields.append("".join(output))
    return fields


def copy_tables(artifact):
    tables, current = {}, None
    for line in artifact.decode("utf-8").split("\n"):
        if line.startswith("COPY "):
            match = re.fullmatch(r'COPY public\."((?:[^"]|"")*)" \(.*\) FROM stdin;', line)
            assert match, "unexpected COPY syntax"
            current = match[1].replace('""', '"')
            tables[current] = []
        elif line == r"\.":
            current = None
        elif current is not None:
            tables[current].append(copy_fields(line))
    return tables


def check_fixture(root, source, destination, artifact, report):
    assert artifact == (HERE / "fixtures/expected.sql").read_bytes()
    assert report["tableCount"] == 2 and report["rowCount"] == 3
    assert report["skippedIndexes"] == 1
    tables = copy_tables(artifact)
    assert set(tables) == {"orders", 'Line "Items"'}
    first, second = tables["orders"]
    assert first[0] == "18446744073709551615"
    assert first[1] == "12345678901234567890123456789012345.123456789012345678901234567890"
    assert first[2] == "Olá 😀\tline\nquote' slash\\"
    assert first[3] == r"\N" and second[3] is None
    assert first[4] == r"\x000a5cff" and first[5] == r"\x"
    assert base64.b64encode(bytes.fromhex(first[4][2:])).decode() == "AApc/w=="
    assert second[2] == "" and second[5] is None
    assert first[6:9] == ["2024-02-29", "2024-02-29 12:34:56.123456", "2024-02-29 12:34:56.123456+00:00"]
    assert tables['Line "Items"'] == [["18446744073709551615", "x:%", "1"]]
    for plan in report["tables"]:
        assert plan["copyRows"] == plan["sourceRows"] == len(tables[plan["sourceTable"]])
    orders = next(p for p in report["tables"] if p["sourceTable"] == "orders")
    assert orders["columns"][0]["unsigned"] is True
    assert orders["columns"][1]["type_arguments"] == [65, 30]
    assert orders["columns"][9]["members"] == ["new", "paid", "a'b"]
    assert all(not c["enforced"] for p in report["tables"] for c in p["constraints"])
    again = invoke(source, root / "again", flavor=report["sourceDeclaration"])
    assert again.returncode == 0, again.stderr
    assert (root / "again/import.sql").read_bytes() == artifact
    assert (root / "again/report.json").read_bytes() == (destination / "report.json").read_bytes()
    preview = invoke(source, root / "preview.json", "dry-run", flavor=report["sourceDeclaration"])
    assert preview.returncode == 0, preview.stderr
    assert (root / "preview.json").read_bytes() == (destination / "report.json").read_bytes()
    assert (root / "preview.json").stat().st_mode & 0o077 == 0
    assert not (root / "preview.sql").exists()
    repeat = invoke(source, destination)
    assert repeat.returncode == 1 and "[destination_exists]" in repeat.stderr
    assert (destination / "import.sql").read_bytes() == artifact
    repeat_preview = invoke(source, root / "preview.json", "dry-run")
    assert repeat_preview.returncode == 1 and "[destination_exists]" in repeat_preview.stderr


def check_value(expected):
    def check(root, source, destination, artifact, report):
        assert copy_tables(artifact)["t"][0][1] == expected
    return check


def large_records():
    value = ("😀漢字'/*;--\\\t\n" * 12000)
    escaped = value.replace("\\", "\\\\").replace("'", "\\'").replace("\t", "\\t").replace("\n", "\\n")
    exercise(dump("(1,'" + escaped + "')", type="longtext"), check=check_value(value))


def large_insert():
    count = 2500
    rows = ",".join(f"({i},'" + "z" * 600 + "')" for i in range(count))
    def check(root, source, destination, artifact, report):
        assert report["rowCount"] == count
        assert len(copy_tables(artifact)["t"]) == count
    exercise(dump(rows), check=check)


def composite_collision():
    schema = "CREATE TABLE t (a varchar(40) NOT NULL, b varchar(40) NOT NULL, PRIMARY KEY (a,b)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;"
    exercise(PREFIX + schema + "INSERT INTO t (a,b) VALUES ('x:y','z'),('x','y:z'),('x%3Ay','z');", check=lambda r, s, d, a, p: (
        require_count(p, 3)
    ))


def require_count(report, count):
    assert report["rowCount"] == count


def default_metadata():
    literal = "x" * 400000
    text = PREFIX + "\n".join(SCHEMA.format(type="longtext DEFAULT '" + literal + "'").replace("`t`", f"`t{i}`") for i in range(8))
    def check(root, source, destination, artifact, report):
        assert report["rowCount"] == 0 and report["tableCount"] == 8
        assert (destination / "report.json").stat().st_size < 50000
        for plan in report["tables"]:
            default = plan["columns"][1]["attributes"][0]["default"]
            assert "value" not in default and default["omitted"] is True
            assert default["decodedBytes"] == len(literal)
            assert default["decodedSha256"] == hashlib.sha256(literal.encode()).hexdigest()
    exercise(text, check=check, flavor="mariadb-10.11")


def metadata_limit():
    members = ",".join("'" + str(i) + "x" * 120 + "'" for i in range(1000))
    text = PREFIX + "\n".join(SCHEMA.format(type="enum(" + members + ")").replace("`t`", f"`t{i}`") for i in range(6))
    exercise(text, expected_code="metadata_too_large")


def cases():
    fixture = (HERE / "fixtures/synthetic.sql").read_bytes()
    result = [("synthetic-" + flavor, lambda f=flavor: exercise(fixture, check=check_fixture, flavor=f)) for flavor in (
        "mysql-8.0", "mysql-8.4", "mariadb-10.11", "mariadb-11.4"
    )]
    successes = [
        ("signed-boundaries", dump("(-9223372036854775808,'x'),(9223372036854775807,'y')")),
        ("unsigned-small", dump("(1,4294967295)", "int unsigned")),
        ("decimal-fixed", dump("(1,'-999.00')", "decimal(5,2)")),
        ("date-leap", dump("(1,'2000-02-29')", "date")),
        ("set-order", dump("(1,'a,c')", "set('a','b','c')")),
        ("enum-empty-member", dump("(1,'')", "enum('','a')")),
        ("literal-comment", dump("(1,'/* SELECT x; */ -- data #')")),
        ("no-backslash", dump("(1,'a\\b''c')", prefix=PREFIX.replace("NO_AUTO_VALUE_ON_ZERO", "NO_BACKSLASH_ESCAPES"))),
        ("saved-session", "SET @OLD_SQL_MODE=@@SQL_MODE; SET @OLD_TIME_ZONE=@@TIME_ZONE;" + dump("(1,'x')") + "SET SQL_MODE=@OLD_SQL_MODE; SET TIME_ZONE=@OLD_TIME_ZONE;"),
        ("multiple-inserts", dump("(1,'x')") + "INSERT INTO t (v,id) VALUES ('y',2);"),
        ("empty-table", PREFIX + SCHEMA.format(type="text")),
        ("current-default", dump("(1,'2024-01-01 00:00:00')", "timestamp DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP")),
        ("implicit-key-not-null", PREFIX + "CREATE TABLE t (id int, v text, PRIMARY KEY (id)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4; INSERT INTO t (id,v) VALUES (1,NULL);"),
        ("utf8-column-collation", dump("(1,'漢字')", "varchar(10) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin")),
    ]
    result.extend((name, lambda text=text: exercise(text)) for name, text in successes)
    failures = [
        ("missing-charset", dump("(1,'x')", prefix=PREFIX.replace("SET NAMES utf8mb4;", "")), "missing_utf8mb4_declaration"),
        ("missing-mode", dump("(1,'x')", prefix=PREFIX.replace("SET SQL_MODE='NO_AUTO_VALUE_ON_ZERO';", "")), "missing_sql_mode_declaration"),
        ("missing-utc", dump("(1,'x')", prefix=PREFIX.replace("SET TIME_ZONE='+00:00';", "")), "missing_utc_declaration"),
        ("non-utc", dump("(1,'x')", prefix=PREFIX.replace("+00:00", "+03:00")), "unsupported_time_zone"),
        ("unknown-mode", dump("(1,'x')", prefix=PREFIX.replace("NO_AUTO_VALUE_ON_ZERO", "ANSI_QUOTES")), "unsupported_sql_mode"),
        ("wrong-charset", dump("(1,'x')", prefix=PREFIX.replace("utf8mb4", "latin1")), "unsupported_charset"),
        ("wrong-column-charset", dump("(1,'x')", "varchar(5) CHARACTER SET latin1"), "unsupported_charset"),
        ("restored-unknown-mode", "SET @OLD_SQL_MODE=@@SQL_MODE;" + dump("(1,'x')") + "SET SQL_MODE=@OLD_SQL_MODE; INSERT INTO t (id,v) VALUES (2,'x');", "missing_sql_mode_declaration"),
        ("restored-unknown-charset", "SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT;" + dump("(1,'x')") + "SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT; INSERT INTO t (id,v) VALUES (2,'x');", "missing_utf8mb4_declaration"),
        ("unknown-session", PREFIX + "SET SECRET_VARIABLE=1;", "unsupported_set"),
        ("missing-column", PREFIX + SCHEMA.format(type="text") + "INSERT INTO t (id) VALUES (1);", "missing_or_duplicate_column"),
        ("duplicate-column", PREFIX + SCHEMA.format(type="text") + "INSERT INTO t (id,id) VALUES (1,2);", "missing_or_duplicate_column"),
        ("explicit-null", dump("(1,NULL)", "text NOT NULL"), "null_not_allowed"),
        ("duplicate-id", dump("(1,'a'),(1,'b')"), "duplicate_document_id"),
        ("signed-overflow", dump("(9223372036854775808,'x')"), "integer_out_of_range"),
        ("unsigned-overflow", dump("(1,18446744073709551616)", "bigint unsigned"), "integer_out_of_range"),
        ("negative-unsigned", dump("(1,-1)", "bigint unsigned"), "integer_out_of_range"),
        ("tiny-overflow", dump("(1,128)", "tinyint(1)"), "integer_out_of_range"),
        ("quoted-integer", dump("(1,'12')", "int"), "invalid_integer"),
        ("decimal-overflow", dump("(1,1000.00)", "decimal(5,2)"), "decimal_out_of_range"),
        ("decimal-scale", dump("(1,1.234)", "decimal(5,2)"), "decimal_out_of_range"),
        ("decimal-exponent", dump("(1,1e2)", "decimal(5,2)"), "invalid_decimal"),
        ("decimal-unicode", dump("(1,'١.00')", "decimal(5,2)"), "invalid_decimal"),
        ("string-limit", dump("(1,'long')", "varchar(3)"), "string_out_of_range"),
        ("text-byte-limit", dump("(1,'" + "é" * 128 + "')", "tinytext"), "string_out_of_range"),
        ("binary-type-limit", dump("(1,0x00)", "binary(256)"), "invalid_type_size"),
        ("duplicate-attribute", dump("(1,'x')", "text DEFAULT '' DEFAULT ''"), "duplicate_column_attribute"),
        ("enum-invalid", dump("(1,'other')", "enum('a','b')"), "invalid_enum"),
        ("enum-error-sentinel", dump("(1,'')", "enum('a','b')"), "invalid_enum"),
        ("set-order-invalid", dump("(1,'b,a')", "set('a','b')"), "invalid_set"),
        ("set-duplicate", dump("(1,'a,a')", "set('a','b')"), "invalid_set"),
        ("binary-string", dump("(1,'bytes')", "blob"), "binary_requires_hex"),
        ("binary-odd", dump("(1,0x0)", "blob"), "binary_requires_hex"),
        ("binary-length", dump("(1,0xffff)", "binary(1)"), "binary_out_of_range"),
        ("text-nul", dump(r"(1,'x\0y')"), "invalid_string"),
        ("zero-date", dump("(1,'0000-00-00')", "date"), "invalid_date"),
        ("zero-month", dump("(1,'2024-00-01')", "date"), "invalid_date"),
        ("invalid-leap", dump("(1,'2023-02-29')", "date"), "invalid_date"),
        ("timestamp-offset", dump("(1,'2024-01-01 00:00:00+03:00')", "timestamp"), "invalid_date"),
        ("timestamp-precision", dump("(1,'2024-01-01 00:00:00.123456')", "timestamp(3)"), "invalid_timestamp_precision"),
        ("timestamp-range", dump("(1,'2040-01-01 00:00:00')", "timestamp"), "timestamp_out_of_range"),
        ("time-range", dump("(1,'839:00:00')", "time"), "invalid_time"),
        ("float-unsupported", dump("(1,1.25)", "double"), "unsupported_type"),
        ("json-unsupported", dump("(1,'{}')", "json"), "unsupported_type"),
        ("generated-column", dump("(1,'x')", "text GENERATED ALWAYS AS (uuid()) STORED"), "unsupported_column_attribute"),
        ("expression-default", dump("(1,'x')", "text DEFAULT (uuid())"), "unsupported_default"),
        ("nontransactional", dump("(1,'x')").replace("InnoDB", "MyISAM"), "unsupported_engine"),
        ("table-option", dump("(1,'x')").replace("CHARSET=utf8mb4", "CHARSET=utf8mb4 ROW_FORMAT=DYNAMIC"), "unsupported_table_option"),
        ("no-key", dump("(1,'x')").replace(", PRIMARY KEY (`id`)", ""), "missing_or_multiple_primary_key"),
        ("binary-key", PREFIX + "CREATE TABLE t (id blob, PRIMARY KEY (id)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;", "unsupported_primary_key"),
        ("duplicate-schema-column", dump("(1,'x')").replace("`v` text", "`ID` text"), "duplicate_column"),
        ("prefix-index", dump("(1,'x')").replace("PRIMARY KEY (`id`)", "PRIMARY KEY (`id`), UNIQUE KEY u (`v`(2))"), "unsupported_definition"),
        ("unknown-fk", dump("(1,'x')").replace("PRIMARY KEY (`id`)", "PRIMARY KEY (`id`), FOREIGN KEY (`id`) REFERENCES missing (`id`)"), "unknown_foreign_key_target"),
        ("collection-collision", PREFIX + SCHEMA.format(type="text") + SCHEMA.format(type="text").replace("`t`", "`T`"), "collection_name_collision"),
        ("reserved-table", dump("(1,'x')").replace("`t`", "`sqlite_custom`"), "reserved_collection_name"),
        ("identifier-byte-limit", dump("(1,'x')").replace("`t`", "`" + "é" * 32 + "`"), "identifier_too_long"),
        ("identifier-control", dump("(1,'x')").replace("`t`", "`t\x85`"), "invalid_identifier"),
        ("control-key", PREFIX + "CREATE TABLE t (id varchar(10), PRIMARY KEY (id)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4; INSERT INTO t (id) VALUES ('x\x85');", "invalid_document_id"),
        ("long-key", PREFIX + "CREATE TABLE t (id varchar(200), PRIMARY KEY (id)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4; INSERT INTO t (id) VALUES ('" + "😀" * 129 + "');", "invalid_document_id"),
        ("value-function", dump("(1,uuid())"), "invalid_string"),
        ("insert-ignore", dump("(1,'x')").replace("INSERT INTO", "INSERT IGNORE INTO"), "unexpected_token"),
        ("insert-expression", dump("(1,CONCAT('a','b'))"), "invalid_string"),
        ("row-too-short", dump("(1)"), "unexpected_token"),
        ("row-too-long", dump("(1,'a','b')"), "unexpected_token"),
        ("unterminated-string", dump("(1,'broken)"), "unterminated_quote"),
        ("missing-semicolon", dump("(1,'x')").rstrip().removesuffix(";"), "unexpected_token"),
        ("malformed-number", dump("(1,1-2)", "int"), "invalid_number"),
        ("unknown-escape", dump(r"(1,'a\q')"), "unsupported_escape"),
        ("nested-version-comment", "/*!40101 /* nested */ SET NAMES utf8mb4 */;", "nested_comment"),
        ("unclosed-comment", PREFIX + "/* truncated", "unterminated_comment"),
        ("unclosed-version", PREFIX + "/*!40101 SET SQL_NOTES=0", "unterminated_comment"),
        ("future-version-data", PREFIX + SCHEMA.format(type="text") + "/*!99999 INSERT INTO t (id,v) VALUES (1,'x') */;", "unsupported_version_comment"),
        ("active-version-data", PREFIX + SCHEMA.format(type="text") + "/*!40101 INSERT INTO t (id,v) VALUES (1,'x') */;", "unsupported_version_comment"),
        ("future-version-mode", PREFIX + "/*!99999 SET SQL_MODE='NO_BACKSLASH_ESCAPES' */;" + SCHEMA.format(type="text"), "unsupported_version_comment"),
        ("ambiguous-mysql-version", PREFIX + "/*!80036 SET SQL_MODE='' */;", "unsupported_version_comment"),
        ("ambiguous-maria-version", PREFIX + "/*M!101150 SET SQL_MODE='' */;", "unsupported_version_comment"),
        ("version-multiple-statements", PREFIX + "/*!40101 SET SQL_NOTES=0; INSERT INTO t (id,v) VALUES (1,'x') */;", "unsupported_version_comment"),
        ("fake-sandbox", "/*M!999999\\! system command */" + dump("(1,'x')"), "unsupported_sandbox_directive"),
        ("utf8-invalid", dump("(1,'x')").encode() + b"\xff", "file_encoding_or_io_error"),
        ("bom-rejected", b"\xef\xbb\xbf" + dump("(1,'x')").encode(), "unsupported_token"),
        ("utf16-rejected", dump("(1,'x')").encode("utf-16"), "file_encoding_or_io_error"),
        ("oversize-document", dump("(1,'" + "x" * (1 << 20) + "')", "longtext"), "document_too_large"),
    ]
    for keyword in ("DROP TABLE t", "REPLACE INTO t VALUES (1)", "LOAD DATA INFILE 'synthetic' INTO TABLE t", "SELECT 1", "USE another", "DELIMITER $$", "CALL routine()", "COMMIT"):
        failures.append(("statement-" + keyword.split()[0].lower(), PREFIX + keyword + ";", "unsupported_statement"))
    for object in ("PROCEDURE p() BEGIN SELECT 1; END", "FUNCTION f() RETURNS INT RETURN 1", "VIEW v AS SELECT 1", "TRIGGER trg BEFORE INSERT ON t FOR EACH ROW SET @x=1"):
        failures.append(("object-" + object.split()[0].lower(), PREFIX + "CREATE " + object + ";", "unsupported_definition"))
    failures.append(("shell-meta", PREFIX + r"\! touch synthetic" + "\n", "unsupported_token"))
    result.extend((name, lambda text=text, code=code: exercise(text, expected_code=code)) for name, text, code in failures)
    result.extend([
        ("large-chunked-record", large_records), ("large-multirow-insert", large_insert),
        ("composite-id-escaping", composite_collision),
        ("control-copy-escaping", lambda: exercise(dump(r"(1,'a\b\t\n\r\Z')"), check=check_value("a\b\t\n\r\x1a"))),
        ("no-backslash-content", lambda: exercise(dump("(1,'a\\b''c')", prefix=PREFIX.replace("NO_AUTO_VALUE_ON_ZERO", "NO_BACKSLASH_ESCAPES")), check=check_value("a\\b'c"))),
        ("maria-sandbox", lambda: exercise("/*M!999999\\- enable the sandbox mode */\n" + dump("(1,'x')"), flavor="mariadb-11.4")),
        ("large-default-metadata", default_metadata), ("aggregate-metadata-limit", metadata_limit),
    ])
    return result


def main():
    work = cases()
    failed = []
    with ThreadPoolExecutor() as executor:
        futures = {executor.submit(test): name for name, test in work}
        for future in as_completed(futures):
            try:
                future.result()
            except Exception as error:
                failed.append(futures[future])
                print(f"FAIL {futures[future]}: {error}", file=sys.stderr)
    print(f"{len(work) - len(failed)}/{len(work)} parallel synthetic CLI checks passed")
    return bool(failed)


if __name__ == "__main__":
    sys.exit(main())
