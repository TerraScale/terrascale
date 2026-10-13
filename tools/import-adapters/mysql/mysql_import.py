#!/usr/bin/env python3
"""Parse a narrow MySQL dump profile without executing its SQL. Standard library only."""

import argparse
import base64
import codecs
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import sqlite3
import stat
import sys
import tempfile
import unicodedata
from dataclasses import dataclass, field


PROFILE = "mysql-copy-v1"
CONTRACT = "62f251f6beb8c4075dbba6f59a6a97c66487b754"
FLAVORS = ("mysql-8.0", "mysql-8.4", "mariadb-10.11", "mariadb-11.4")
MAX_SOURCE = 1 << 30
MAX_DOCUMENT = 1 << 20
MAX_TOKEN = 2 << 20
MAX_METADATA = 512 << 10
MAX_REPORT = 4 << 20
CHUNK = 65536
MODES = {
    "NO_AUTO_VALUE_ON_ZERO", "NO_BACKSLASH_ESCAPES", "STRICT_TRANS_TABLES",
    "STRICT_ALL_TABLES", "NO_ZERO_DATE", "NO_ZERO_IN_DATE",
    "ERROR_FOR_DIVISION_BY_ZERO", "NO_ENGINE_SUBSTITUTION", "ONLY_FULL_GROUP_BY",
}


class ConversionError(Exception):
    def __init__(self, code, line=0):
        self.code = code
        self.line = line
        super().__init__(code)


def require(condition, code):
    if not condition:
        raise ConversionError(code)


@dataclass(frozen=True)
class Token:
    kind: str
    value: str
    line: int

    def is_word(self, value):
        return self.kind == "word" and self.value.upper() == value


class Lexer:
    def __init__(self, source, flavor):
        self.source = source
        self.flavor = flavor
        self.digest = hashlib.sha256()
        self.decoder = codecs.getincrementaldecoder("utf-8")("strict")
        self.buffer = ""
        self.position = 0
        self.eof = False
        self.line = 1
        self.no_backslash = None
        self.version_comment = False
        self.saved = None
        self.bytes_read = 0
        self.sandbox_directives = 0
        self.version_set_pending = False

    def char(self, offset=0):
        while len(self.buffer) - self.position <= offset and not self.eof:
            raw = self.source.read(CHUNK)
            self.bytes_read += len(raw)
            require(self.bytes_read <= MAX_SOURCE, "source_too_large")
            self.digest.update(raw)
            self.buffer = self.buffer[self.position:] + self.decoder.decode(raw, final=not raw)
            self.position = 0
            self.eof = not raw
        index = self.position + offset
        return self.buffer[index] if index < len(self.buffer) else ""

    def take(self):
        char = self.char()
        self.position += bool(char)
        self.line += char == "\n"
        return char

    def token(self):
        if self.saved is not None:
            token, self.saved = self.saved, None
            return token
        while self.char():
            if self.char().isspace():
                self.take()
            elif self.version_comment and self.char() == "*" and self.char(1) == "/":
                require(not self.version_set_pending, "unsupported_version_comment")
                self.take()
                self.take()
                self.version_comment = False
            elif self.char() == "#" or (
                self.char() == "-" and self.char(1) == "-" and self.char(2).isspace()
            ):
                while self.char() and self.take() != "\n":
                    pass
            elif self.char() == "/" and self.char(1) == "*":
                require(not self.version_comment, "nested_comment")
                self.take()
                self.take()
                executable = self.char() == "!"
                maria = self.char() == "M" and self.char(1) == "!"
                if maria:
                    self.take()
                    executable = True
                if executable:
                    self.take()
                    digits = ""
                    while self.char().isdigit():
                        digits += self.take()
                        require(len(digits) <= 6, "invalid_version_comment")
                    require(len(digits) in (5, 6), "invalid_version_comment")
                    if maria and digits == "999999":
                        require(self.flavor.startswith("mariadb-"), "unsupported_sandbox_directive")
                        marker = ""
                        while not (self.char() == "*" and self.char(1) == "/"):
                            require(bool(self.char()) and len(marker) < 128, "unsupported_sandbox_directive")
                            marker += self.take()
                        require(marker.strip() == r"\- enable the sandbox mode", "unsupported_sandbox_directive")
                        self.take()
                        self.take()
                        self.sandbox_directives += 1
                        continue
                    # These legacy SET wrappers are active in every declared exporter family.
                    require(not maria and digits in ("40014", "40101", "40103", "40111", "50503"), "unsupported_version_comment")
                    self.version_comment = True
                    self.version_set_pending = True
                else:
                    while not (self.char() == "*" and self.char(1) == "/"):
                        require(bool(self.take()), "unterminated_comment")
                    self.take()
                    self.take()
            else:
                break
        line = self.line
        if self.version_set_pending:
            require("".join(self.char(i) for i in range(3)).upper() == "SET" and self.char(3).isspace(), "unsupported_version_comment")
            for _ in range(3):
                self.take()
            self.version_set_pending = False
            return Token("word", "SET", line)
        char = self.take()
        require(not self.version_comment or char != ";", "unsupported_version_comment")
        if not char:
            require(not self.version_comment, "unterminated_comment")
            return Token("eof", "", line)
        if char in "'`":
            value = []
            length = 0
            while True:
                next_char = self.take()
                require(bool(next_char), "unterminated_quote")
                if next_char == char:
                    if self.char() != char:
                        break
                    self.take()
                elif next_char == "\\" and char == "'" and self.no_backslash is not True:
                    require(self.no_backslash is not None, "missing_sql_mode_declaration")
                    escaped = self.take()
                    escapes = {
                        "0": "\0", "b": "\b", "n": "\n", "r": "\r", "t": "\t",
                        "Z": "\x1a", "\\": "\\", "'": "'", '"': '"',
                    }
                    require(escaped in escapes, "unsupported_escape")
                    next_char = escapes[escaped]
                value.append(next_char)
                length += len(next_char.encode("utf-8"))
                require(length <= MAX_TOKEN, "token_too_large")
            return Token("string" if char == "'" else "identifier", "".join(value), line)
        if char.isascii() and (char.isalpha() or char in "_$"):
            value = char
            while self.char() and self.char().isascii() and (
                self.char().isalnum() or self.char() in "_$"
            ):
                value += self.take()
                require(len(value) <= MAX_TOKEN, "token_too_large")
            return Token("word", value, line)
        if char.isascii() and (char.isdigit() or char in "+-"):
            value = char
            if char == "0" and self.char() in ("x", "X"):
                value += self.take()
                while self.char() and self.char() in "0123456789abcdefABCDEF":
                    value += self.take()
                    require(len(value) <= MAX_TOKEN, "token_too_large")
                return Token("hex", value[2:], line)
            while self.char() and self.char() in "0123456789.eE+-":
                value += self.take()
                require(len(value) <= MAX_TOKEN, "token_too_large")
            require(bool(re.fullmatch(r"[+-]?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?", value)), "invalid_number")
            return Token("number", value, line)
        require(char in "(),;=.@", "unsupported_token")
        return Token("symbol", char, line)

    def peek(self):
        if self.saved is None:
            self.saved = self.token()
        return self.saved

    def expect(self, value):
        token = self.token()
        require(token.is_word(value) or token.kind == "symbol" and token.value == value, "unexpected_token")
        return token

    def statement(self):
        tokens = []
        length = 0
        while True:
            token = self.token()
            require(token.kind != "eof", "missing_semicolon")
            if token.value == ";" and token.kind == "symbol":
                return tokens
            tokens.append(token)
            length += len(token.value.encode("utf-8")) + 2
            require(length <= MAX_DOCUMENT, "statement_too_large")


def identifier(token):
    require(token.kind in ("word", "identifier"), "invalid_identifier")
    name = token.value
    require(bool(name) and len(name.encode("utf-8")) <= 63, "identifier_too_long")
    require(not any(unicodedata.category(c) == "Cc" for c in name), "invalid_identifier")
    return name


def quoted(name):
    return '"' + name.replace('"', '""') + '"'


def split_groups(tokens):
    result, current, depth = [], [], 0
    for token in tokens:
        if token.kind == "symbol":
            depth += token.value == "("
            depth -= token.value == ")"
            require(depth >= 0, "unbalanced_parentheses")
            if token.value == "," and depth == 0:
                require(bool(current), "empty_definition")
                result.append(current)
                current = []
                continue
        current.append(token)
    require(depth == 0 and bool(current), "unbalanced_parentheses")
    return result + [current]


class Definition:
    def __init__(self, tokens):
        self.tokens = iter(tokens)
        self.current = next(self.tokens, None)

    def pop(self):
        require(self.current is not None, "incomplete_definition")
        token = self.current
        self.current = next(self.tokens, None)
        return token

    def accept(self, word):
        if self.current and (self.current.is_word(word) or (
            self.current.kind == "symbol" and self.current.value == word
        )):
            self.pop()
            return True
        return False

    def expect(self, word):
        require(self.accept(word), "unsupported_definition")

    def integer(self):
        token = self.pop()
        require(token.kind == "number" and token.value.isascii() and token.value.isdigit(), "invalid_type_size")
        require(len(token.value) <= 6, "invalid_type_size")
        return int(token.value)

    def names(self):
        self.expect("(")
        names = [identifier(self.pop())]
        while self.accept(","):
            names.append(identifier(self.pop()))
        self.expect(")")
        require(len(set(n.casefold() for n in names)) == len(names), "duplicate_key_column")
        return names


@dataclass
class Column:
    name: str
    source_type: str
    pg_type: str
    mapping: str
    nullable: bool = True
    unsigned: bool = False
    size: int | None = None
    scale: int = 0
    members: list = field(default_factory=list)
    attributes: list = field(default_factory=list)
    type_arguments: list = field(default_factory=list)
    max_bytes: int | None = None


def parse_column(tokens):
    definition = Definition(tokens)
    name = identifier(definition.pop())
    type_token = definition.pop()
    require(type_token.kind == "word", "unsupported_type")
    source_type = type_token.value.upper()
    sizes, members = [], []
    if definition.accept("("):
        if source_type in ("ENUM", "SET"):
            while True:
                value = definition.pop()
                require(value.kind == "string" and "\0" not in value.value, "invalid_enum_definition")
                members.append(value.value)
                require(len(members) <= 4096, "too_many_enum_members")
                if not definition.accept(","):
                    break
        else:
            sizes.append(definition.integer())
            if definition.accept(","):
                sizes.append(definition.integer())
        definition.expect(")")
    unsigned = definition.accept("UNSIGNED")
    integers = {"TINYINT": 8, "SMALLINT": 16, "MEDIUMINT": 24, "INT": 32, "INTEGER": 32, "BIGINT": 64}
    if source_type in integers:
        require(len(sizes) <= 1 and (not sizes or 1 <= sizes[0] <= 255), "invalid_type_size")
        wide_unsigned = unsigned and source_type == "BIGINT"
        column = Column(name, source_type, "text" if wide_unsigned else "bigint",
                        "unsigned_integer_string" if wide_unsigned else "integer", size=integers[source_type])
    elif source_type in ("DECIMAL", "NUMERIC"):
        require(len(sizes) <= 2, "invalid_type_size")
        precision, scale = (sizes[0] if sizes else 10), (sizes[1] if len(sizes) == 2 else 0)
        require(1 <= precision <= 65 and 0 <= scale <= min(30, precision), "invalid_type_size")
        column = Column(name, source_type, "numeric", "decimal_string", size=precision, scale=scale)
    elif source_type in ("BINARY", "VARBINARY", "TINYBLOB", "BLOB", "MEDIUMBLOB", "LONGBLOB"):
        if source_type in ("BINARY", "VARBINARY"):
            require(len(sizes) == 1 and 1 <= sizes[0] <= (255 if source_type == "BINARY" else 65535), "invalid_type_size")
            size = sizes[0]
        else:
            require(not sizes, "invalid_type_size")
            size = {"TINYBLOB": 255, "BLOB": 65535, "MEDIUMBLOB": 16777215, "LONGBLOB": 4294967295}[source_type]
        column = Column(name, source_type, "bytea", "base64", size=size)
    elif source_type in ("CHAR", "VARCHAR", "TINYTEXT", "TEXT", "MEDIUMTEXT", "LONGTEXT", "ENUM", "SET"):
        if source_type in ("CHAR", "VARCHAR"):
            require(len(sizes) == 1 and 1 <= sizes[0] <= (255 if source_type == "CHAR" else 65535), "invalid_type_size")
        else:
            require(not sizes, "invalid_type_size")
        if source_type in ("ENUM", "SET"):
            require(bool(members) and len(set(members)) == len(members), "invalid_enum_definition")
            require(source_type != "SET" or (len(members) <= 64 and all(m and "," not in m for m in members)), "invalid_set_definition")
        column = Column(name, source_type, "text", "string", size=sizes[0] if sizes else None, members=members)
        column.max_bytes = {"TINYTEXT": 255, "TEXT": 65535, "MEDIUMTEXT": 16777215, "LONGTEXT": 4294967295}.get(source_type)
    elif source_type in ("DATE", "DATETIME", "TIMESTAMP", "TIME"):
        require(len(sizes) <= 1 and (not sizes or source_type != "DATE" and 0 <= sizes[0] <= 6), "invalid_type_size")
        pg_type = {"DATE": "date", "DATETIME": "timestamp", "TIMESTAMP": "timestamptz", "TIME": "text"}[source_type]
        column = Column(name, source_type, pg_type, pg_type, scale=sizes[0] if sizes else 0)
    else:
        raise ConversionError("unsupported_type")
    require(not unsigned or source_type in integers or source_type in ("DECIMAL", "NUMERIC"), "unsupported_unsigned")
    column.unsigned = unsigned
    column.type_arguments = sizes
    null_seen = False
    while definition.current:
        if definition.accept("NOT"):
            definition.expect("NULL")
            require(not null_seen, "duplicate_nullability")
            column.nullable, null_seen = False, True
        elif definition.accept("NULL"):
            require(not null_seen, "duplicate_nullability")
            null_seen = True
        elif definition.current.is_word("DEFAULT") or definition.current.is_word("ON"):
            previous = definition.pop()
            attribute = "default"
            if previous.is_word("ON"):
                definition.expect("UPDATE")
                attribute = "on_update"
            require(not any(attribute in a for a in column.attributes), "duplicate_column_attribute")
            value = definition.pop()
            require(value.kind in ("string", "number", "hex") or value.is_word("NULL") or value.is_word("CURRENT_TIMESTAMP"), "unsupported_default")
            require(attribute != "on_update" or value.is_word("CURRENT_TIMESTAMP"), "unsupported_default")
            expression = {"kind": value.kind}
            if value.kind in ("string", "number", "hex"):
                literal = value.value.encode("utf-8")
                expression.update({"omitted": True, "decodedBytes": len(literal), "decodedSha256": hashlib.sha256(literal).hexdigest()})
            else:
                expression["value"] = value.value.upper()
            if value.is_word("CURRENT_TIMESTAMP"):
                require(source_type in ("TIMESTAMP", "DATETIME"), "unsupported_default")
                if definition.accept("("):
                    expression["precision"] = definition.integer()
                    require(expression["precision"] <= 6, "invalid_type_size")
                    definition.expect(")")
            column.attributes.append({attribute: expression})
        elif definition.accept("AUTO_INCREMENT"):
            require(source_type in integers, "unsupported_auto_increment")
            column.attributes.append({"auto_increment": True})
        elif definition.accept("CHARACTER"):
            definition.expect("SET")
            require(definition.pop().is_word("UTF8MB4"), "unsupported_charset")
            require(column.mapping == "string", "unsupported_charset")
            column.attributes.append({"charset": "utf8mb4"})
        elif definition.accept("COLLATE"):
            collation = identifier(definition.pop())
            require(bool(re.fullmatch(r"utf8mb4_[a-z0-9_]+", collation)), "unsupported_collation")
            require(column.mapping == "string", "unsupported_collation")
            column.attributes.append({"collation": collation})
        elif definition.accept("COMMENT"):
            require(definition.pop().kind == "string", "unsupported_definition")
            column.attributes.append({"comment_omitted": True})
        else:
            raise ConversionError("unsupported_column_attribute")
    attributes = [next(iter(a)) for a in column.attributes]
    require(len(set(attributes)) == len(attributes), "duplicate_column_attribute")
    return column


@dataclass
class Table:
    name: str
    collection: str
    columns: list
    constraints: list
    options: dict
    rows: int = 0

    @property
    def primary_key(self):
        keys = [c for c in self.constraints if c["kind"] == "primary"]
        require(len(keys) == 1, "missing_or_multiple_primary_key")
        return keys[0]["columns"]


def collection_name(name):
    result = re.sub("[^a-z0-9_-]", "_", name.lower())
    if not re.match("[a-z]", result):
        result = "t_" + result
    result = result[:63]
    require(result not in ("collections", "commit_offset", "terrascale-import") and not result.startswith(("sqlite_", "turso_")), "reserved_collection_name")
    return result


def parse_constraint(tokens):
    definition = Definition(tokens)
    name = None
    if definition.accept("CONSTRAINT"):
        name = identifier(definition.pop())
    if definition.accept("PRIMARY"):
        definition.expect("KEY")
        kind = "primary"
    elif definition.accept("UNIQUE"):
        definition.accept("KEY") or definition.accept("INDEX")
        kind = "unique"
    elif definition.accept("FOREIGN"):
        definition.expect("KEY")
        kind = "foreign"
    elif definition.accept("KEY") or definition.accept("INDEX"):
        kind = "index"
    else:
        raise ConversionError("unsupported_constraint")
    if definition.current and definition.current.value != "(":
        require(name is None, "ambiguous_constraint_name")
        name = identifier(definition.pop())
    columns = definition.names()
    result = {"kind": kind, "name": name, "columns": columns, "enforced": False}
    if kind == "foreign":
        definition.expect("REFERENCES")
        result["references_table"] = identifier(definition.pop())
        result["references_columns"] = definition.names()
        require(len(columns) == len(result["references_columns"]), "foreign_key_arity")
        while definition.accept("ON"):
            event = definition.pop()
            require(event.is_word("DELETE") or event.is_word("UPDATE"), "unsupported_foreign_key_action")
            action = definition.pop()
            require(action.kind == "word" and action.value.upper() in ("CASCADE", "RESTRICT", "SET", "NO"), "unsupported_foreign_key_action")
            value = action.value.upper()
            if value == "SET":
                definition.expect("NULL")
                value += " NULL"
            elif value == "NO":
                definition.expect("ACTION")
                value += " ACTION"
            key = "on_" + event.value.lower()
            require(key not in result, "duplicate_foreign_key_action")
            result[key] = value
    require(definition.current is None, "unsupported_constraint")
    return result


def parse_table(tokens):
    definition = Definition(tokens)
    definition.expect("TABLE")
    name = identifier(definition.pop())
    definition.expect("(")
    body, depth = [], 1
    while depth:
        token = definition.pop()
        if token.kind == "symbol":
            depth += token.value == "("
            depth -= token.value == ")"
        if depth:
            body.append(token)
    columns, constraints = [], []
    for group in split_groups(body):
        if group[0].kind == "word" and group[0].value.upper() in ("CONSTRAINT", "PRIMARY", "UNIQUE", "FOREIGN", "KEY", "INDEX", "CHECK", "FULLTEXT", "SPATIAL"):
            constraints.append(parse_constraint(group))
        else:
            columns.append(parse_column(group))
    require(1 <= len(columns) <= 1600, "too_many_columns")
    names = [c.name for c in columns]
    require(len(set(n.casefold() for n in names)) == len(names), "duplicate_column")
    options = {}
    while definition.current:
        default = definition.accept("DEFAULT")
        option = definition.pop()
        require(option.kind == "word", "unsupported_table_option")
        key = option.value.upper()
        if key == "CHARACTER":
            definition.expect("SET")
            key = "CHARSET"
        require(key in ("ENGINE", "CHARSET", "COLLATE", "AUTO_INCREMENT") and (not default or key in ("CHARSET", "COLLATE")), "unsupported_table_option")
        definition.accept("=")
        value = definition.pop()
        require(key not in options, "duplicate_table_option")
        if key == "AUTO_INCREMENT":
            require(value.kind == "number" and value.value.isdigit() and len(value.value) <= 20, "unsupported_table_option")
        else:
            require(value.kind == "word", "unsupported_table_option")
        options[key] = value.value
    require(options.get("ENGINE", "").upper() == "INNODB", "unsupported_engine")
    require(options.get("CHARSET", "").lower() == "utf8mb4", "unsupported_charset")
    require("COLLATE" not in options or bool(re.fullmatch("utf8mb4_[a-z0-9_]+", options["COLLATE"])), "unsupported_collation")
    table = Table(name, collection_name(name), columns, constraints, options)
    for constraint in constraints:
        require(all(n in names for n in constraint["columns"]), "unknown_key_column")
    for kinds in (("primary", "unique", "index"), ("foreign",)):
        names_in_scope = [c["name"].casefold() for c in constraints if c["kind"] in kinds and c["name"] is not None]
        require(len(set(names_in_scope)) == len(names_in_scope), "duplicate_constraint_name")
    emitted_names = [c["name"].casefold() for c in constraints if c["kind"] != "index" and c["name"] is not None]
    for constraint in constraints:
        name = constraint["name"]
        # PostgreSQL has one constraint namespace; keep reused MySQL names in the report.
        constraint["outputName"] = name if constraint["kind"] != "index" and name and emitted_names.count(name.casefold()) == 1 else None
    for key in table.primary_key:
        column = next(c for c in columns if c.name == key)
        # MySQL primary keys imply NOT NULL even if the column omitted that spelling.
        column.nullable = False
        require(column.mapping in ("integer", "unsigned_integer_string", "string"), "unsupported_primary_key")
    return table


def convert_value(column, token):
    if token.is_word("NULL"):
        require(column.nullable, "null_not_allowed")
        return None, None
    value = token.value
    if column.mapping in ("integer", "unsigned_integer_string"):
        require(token.kind == "number" and bool(re.fullmatch(r"-?(?:0|[1-9]\d*)", value)), "invalid_integer")
        require(len(value) <= 21, "integer_out_of_range")
        number = int(value)
        low, high = (0, (1 << column.size) - 1) if column.unsigned else (-(1 << (column.size - 1)), (1 << (column.size - 1)) - 1)
        require(low <= number <= high, "integer_out_of_range")
        return str(number), str(number) if column.mapping == "unsigned_integer_string" else number
    if column.mapping == "decimal_string":
        require(token.kind in ("number", "string") and bool(re.fullmatch(r"-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?", value)), "invalid_decimal")
        whole, _, fraction = value.lstrip("-").partition(".")
        require(len(whole.lstrip("0")) <= column.size - column.scale and len(fraction) == column.scale, "decimal_out_of_range")
        require(not column.unsigned or not value.startswith("-"), "decimal_out_of_range")
        return value, value
    if column.mapping == "base64":
        require(token.kind == "hex" and len(value) % 2 == 0, "binary_requires_hex")
        raw = bytes.fromhex(value)
        require(len(raw) <= column.size and (column.source_type != "BINARY" or len(raw) == column.size), "binary_out_of_range")
        return "\\x" + value.lower(), base64.b64encode(raw).decode("ascii")
    require(token.kind == "string" and "\0" not in value, "invalid_string")
    if column.mapping == "string":
        require(column.size is None or len(value) <= column.size, "string_out_of_range")
        require(column.max_bytes is None or len(value.encode("utf-8")) <= column.max_bytes, "string_out_of_range")
        if column.source_type == "ENUM":
            require(value in column.members, "invalid_enum")
        if column.source_type == "SET" and value:
            selected = value.split(",")
            require(selected == [m for m in column.members if m in selected], "invalid_set")
        return value, value
    if column.source_type == "TIME":
        match = re.fullmatch(r"(-?)(\d{2,3}):([0-5]\d):([0-5]\d)(?:\.(\d{1,6}))?", value)
        require(match is not None, "invalid_time")
        require(int(match[2]) <= 838 and len(match[5] or "") <= column.scale, "invalid_time")
        return value, value
    pattern = r"(\d{4})-\d{2}-\d{2}"
    if column.source_type != "DATE":
        pattern += r" \d{2}:\d{2}:\d{2}(?:\.(\d{1,6}))?"
    match = re.fullmatch(pattern, value)
    require(match is not None and int(match[1]) >= 1000, "invalid_date")
    require(column.source_type == "DATE" or len(match[2] or "") <= column.scale, "invalid_timestamp_precision")
    try:
        if column.source_type == "DATE":
            datetime.date.fromisoformat(value)
        else:
            datetime.datetime.fromisoformat(value)
    except ValueError:
        raise ConversionError("invalid_date") from None
    if column.source_type == "TIMESTAMP":
        instant = datetime.datetime.fromisoformat(value)
        require(datetime.datetime(1970, 1, 1, 0, 0, 1) <= instant <= datetime.datetime(2038, 1, 19, 3, 14, 7, 999999), "timestamp_out_of_range")
        return value + "+00:00", value.replace(" ", "T") + "Z"
    return value, value.replace(" ", "T")


def copy_escape(value):
    if value is None:
        return r"\N"
    escapes = {"\\": "\\\\", "\t": "\\t", "\n": "\\n", "\r": "\\r", "\b": "\\b", "\f": "\\f", "\v": "\\v"}
    return "".join(escapes.get(c, f"\\{ord(c):03o}" if ord(c) < 32 or ord(c) == 127 else c) for c in value)


class Adapter:
    def __init__(self, source, stage, flavor):
        self.lexer = Lexer(source, flavor)
        self.stage = stage
        self.flavor = flavor
        self.tables = {}
        self.settings = {}
        self.variables = {}
        self.modes_seen = set()
        self.skipped_indexes = 0
        self.row_bytes = 0
        self.metadata_bytes = 0
        self.keys = sqlite3.connect(stage / "keys.db")
        # Only this fixed, trusted scratch schema is executed; source SQL is never sent to SQLite.
        self.keys.execute("CREATE TABLE ids (table_name TEXT, id TEXT, PRIMARY KEY (table_name, id)) WITHOUT ROWID")

    def check_settings(self):
        require(self.settings.get("NAMES") == "utf8mb4", "missing_utf8mb4_declaration")
        require(self.settings.get("CHARACTER_SET_CLIENT") == "utf8mb4" and self.settings.get("CHARACTER_SET_RESULTS") == "utf8mb4", "missing_utf8mb4_declaration")
        require(self.settings.get("SQL_MODE") is not None, "missing_sql_mode_declaration")
        require(self.settings.get("TIME_ZONE") == "+00:00", "missing_utc_declaration")

    def session(self, tokens):
        definition = Definition(tokens)
        if definition.accept("NAMES"):
            token = definition.pop()
            require(token.kind in ("word", "string") and token.value.lower() == "utf8mb4", "unsupported_charset")
            if definition.accept("COLLATE"):
                require(bool(re.fullmatch("utf8mb4_[a-z0-9_]+", definition.pop().value)), "unsupported_collation")
            require(definition.current is None, "unsupported_set")
            self.settings["NAMES"] = "utf8mb4"
            self.settings["CHARACTER_SET_CLIENT"] = "utf8mb4"
            self.settings["CHARACTER_SET_RESULTS"] = "utf8mb4"
            return
        definition.accept("SESSION")
        while definition.current:
            variable = definition.accept("@")
            name_token = definition.pop()
            require(name_token.kind == "word", "unsupported_set")
            name = name_token.value.upper()
            definition.expect("=")
            if definition.accept("@"):
                system = definition.accept("@")
                ref = definition.pop()
                if system and ref.is_word("SESSION"):
                    definition.expect(".")
                    ref = definition.pop()
                require(ref.kind == "word", "unsupported_set")
                reference = ref.value.upper()
                if system:
                    require(variable and reference in ("SQL_MODE", "TIME_ZONE", "CHARACTER_SET_CLIENT", "CHARACTER_SET_RESULTS", "COLLATION_CONNECTION", "FOREIGN_KEY_CHECKS", "UNIQUE_CHECKS", "SQL_NOTES"), "unsupported_set")
                    value = self.settings.get(reference)
                else:
                    require(reference in self.variables, "unknown_session_variable")
                    value = self.variables[reference]
            else:
                token = definition.pop()
                require(token.kind in ("string", "number") or token.is_word("NULL") or token.is_word("UTF8MB4"), "unsupported_set")
                value = None if token.is_word("NULL") else token.value
            if variable:
                require(name.startswith(("OLD_", "SAVED_")) and len(self.variables) < 64, "unsupported_session_variable")
                self.variables[name] = value
            elif name == "SQL_MODE":
                self.lexer.no_backslash = None
                if value is not None:
                    modes = value.upper().split(",") if value else []
                    require(len(set(modes)) == len(modes) and set(modes) <= MODES, "unsupported_sql_mode")
                    self.modes_seen.update(modes)
                    self.lexer.no_backslash = "NO_BACKSLASH_ESCAPES" in modes
                self.settings[name] = value
            elif name == "TIME_ZONE":
                require(value in (None, "+00:00"), "unsupported_time_zone")
                self.settings[name] = value
            elif name in ("CHARACTER_SET_CLIENT", "CHARACTER_SET_RESULTS"):
                require(value in (None, "utf8mb4"), "unsupported_charset")
                self.settings[name] = value
            elif name == "COLLATION_CONNECTION":
                require(value is None or bool(re.fullmatch("utf8mb4_[a-z0-9_]+", value)), "unsupported_collation")
                self.settings[name] = value
            elif name in ("FOREIGN_KEY_CHECKS", "UNIQUE_CHECKS", "SQL_NOTES"):
                require(value in (None, "0", "1"), "unsupported_set")
                self.settings[name] = value
            else:
                raise ConversionError("unsupported_set")
            if definition.current:
                definition.expect(",")
                require(definition.current is not None, "unsupported_set")

    def insert(self):
        self.check_settings()
        self.lexer.expect("INTO")
        table_name = identifier(self.lexer.token())
        require(table_name in self.tables, "unknown_table")
        table = self.tables[table_name]
        column_map = {c.name: c for c in table.columns}
        self.lexer.expect("(")
        names = [identifier(self.lexer.token())]
        while self.lexer.peek().value == ",":
            self.lexer.token()
            names.append(identifier(self.lexer.token()))
            require(len(names) <= 1600, "too_many_columns")
        self.lexer.expect(")")
        require(len(names) == len(table.columns) and set(names) == {c.name for c in table.columns}, "missing_or_duplicate_column")
        self.lexer.expect("VALUES")
        with (self.stage / f"rows-{list(self.tables).index(table_name)}").open("a", encoding="utf-8", newline="\n") as rows:
            while True:
                self.lexer.expect("(")
                values = {}
                value_bytes = 0
                for index, name in enumerate(names):
                    if index:
                        self.lexer.expect(",")
                    token = self.lexer.token()
                    value_bytes += len(token.value.encode("utf-8"))
                    require(value_bytes <= 16 << 20, "row_too_large")
                    column = column_map[name]
                    values[name] = convert_value(column, token)
                self.lexer.expect(")")
                document = {c.name: values[c.name][1] for c in table.columns}
                body = json.dumps(document, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
                require(len(body) <= MAX_DOCUMENT, "document_too_large")
                components = [str(document[k]) for k in table.primary_key]
                doc_id = components[0] if len(components) == 1 else ":".join(v.replace("%", "%25").replace(":", "%3A") for v in components)
                require(1 <= len(doc_id.encode("utf-16-le")) // 2 <= 256 and not any(unicodedata.category(c) == "Cc" for c in doc_id), "invalid_document_id")
                try:
                    self.keys.execute("INSERT INTO ids VALUES (?, ?)", (table_name, doc_id))
                except sqlite3.IntegrityError:
                    raise ConversionError("duplicate_document_id") from None
                row = "\t".join(copy_escape(values[c.name][0]) for c in table.columns) + "\n"
                self.row_bytes += len(row.encode("utf-8"))
                require(self.row_bytes <= MAX_SOURCE, "output_too_large")
                rows.write(row)
                table.rows += 1
                if self.lexer.peek().kind != "symbol" or self.lexer.peek().value != ",":
                    break
                self.lexer.token()
        self.lexer.expect(";")

    def parse(self):
        try:
            while True:
                token = self.lexer.token()
                if token.kind == "eof":
                    break
                if token.is_word("SET"):
                    self.session(self.lexer.statement())
                elif token.is_word("CREATE"):
                    self.check_settings()
                    table = parse_table(self.lexer.statement())
                    metadata = {"name": table.name, "columns": [vars(c) for c in table.columns], "constraints": table.constraints, "options": table.options}
                    self.metadata_bytes += len(json.dumps(metadata, ensure_ascii=True, separators=(",", ":")).encode("ascii"))
                    require(self.metadata_bytes <= MAX_METADATA, "metadata_too_large")
                    require(len(self.tables) < 1000, "too_many_tables")
                    require(table.name not in self.tables and not any(t.collection == table.collection for t in self.tables.values()), "collection_name_collision")
                    self.tables[table.name] = table
                elif token.is_word("INSERT"):
                    self.insert()
                else:
                    raise ConversionError("unsupported_statement", token.line)
            require(bool(self.tables), "no_tables")
            for table in self.tables.values():
                for constraint in table.constraints:
                    if constraint["kind"] == "foreign":
                        target = self.tables.get(constraint["references_table"])
                        require(target is not None and all(n in [c.name for c in target.columns] for n in constraint["references_columns"]), "unknown_foreign_key_target")
            return self.write_output()
        except ConversionError as error:
            if not error.line:
                error.line = self.lexer.line
            raise
        finally:
            self.keys.close()

    def write_output(self):
        plans = []
        path = self.stage / "import.sql"
        with path.open("w", encoding="utf-8", newline="\n") as output:
            output.write("SET client_encoding = 'UTF8';\n")
            for table in sorted(self.tables.values(), key=lambda t: t.name):
                columns = ",\n".join("    " + quoted(c.name) + " " + c.pg_type + ("" if c.nullable else " NOT NULL") for c in table.columns)
                schema = f"CREATE TABLE public.{quoted(table.name)} (\n{columns}\n);\n"
                require(len(schema.encode("utf-8")) <= MAX_DOCUMENT, "statement_too_large")
                output.write(schema)
                output.write(f"COPY public.{quoted(table.name)} ({', '.join(quoted(c.name) for c in table.columns)}) FROM stdin;\n")
                row_file = self.stage / f"rows-{list(self.tables).index(table.name)}"
                count, digest = 0, hashlib.sha256()
                if row_file.exists():
                    with row_file.open("rb") as rows:
                        for row in rows:
                            require(len(row) <= 16 << 20 and row.endswith(b"\n"), "invalid_copy_row")
                            require(row.count(b"\t") == len(table.columns) - 1, "copy_column_count_mismatch")
                            digest.update(row)
                            output.write(row.decode("utf-8"))
                            count += 1
                require(count == table.rows, "copy_row_count_mismatch")
                output.write("\\.\n")
                plans.append({
                    "sourceTable": table.name, "outputTable": "public." + quoted(table.name),
                    "collection": table.collection, "sourceRows": table.rows, "copyRows": count,
                    "copyDataSha256": digest.hexdigest(), "primaryKey": table.primary_key,
                    "columns": [vars(c) for c in table.columns], "constraints": table.constraints,
                    "tableOptions": table.options,
                })
            for table in sorted(self.tables.values(), key=lambda t: t.name):
                for constraint in table.constraints:
                    kind = constraint["kind"]
                    if kind == "index":
                        self.skipped_indexes += 1
                        continue
                    columns = ", ".join(quoted(n) for n in constraint["columns"])
                    name = "CONSTRAINT " + quoted(constraint["outputName"]) + " " if constraint["outputName"] else ""
                    if kind == "primary":
                        clause = f"PRIMARY KEY ({columns})"
                    elif kind == "unique":
                        clause = f"UNIQUE ({columns})"
                    else:
                        references = ", ".join(quoted(n) for n in constraint["references_columns"])
                        clause = f"FOREIGN KEY ({columns}) REFERENCES public.{quoted(constraint['references_table'])} ({references})"
                    output.write(f"ALTER TABLE ONLY public.{quoted(table.name)} ADD {name}{clause};\n")
        require(path.stat().st_size <= MAX_SOURCE, "output_too_large")
        return {
            "adapterProfile": PROFILE, "sourceDeclaration": self.flavor,
            "sourceVersionVerified": False, "contractCommit": CONTRACT,
            "outputFormat": "postgres_plain", "sqlModesSeen": sorted(self.modes_seen),
            "sourceCharset": "utf8mb4", "timestampZone": "UTC", "tables": plans,
            "tableCount": len(plans), "rowCount": sum(t.rows for t in self.tables.values()),
            "skippedIndexes": self.skipped_indexes,
            "ignoredSandboxDirectives": self.lexer.sandbox_directives,
            "warnings": [
                "Constraints are metadata only in TerraBase; collation and comparison semantics are lost.",
                "Unsigned BIGINT becomes a digit string; DECIMAL becomes an exact decimal string; TINYINT(1) remains an integer.",
                "ENUM/SET become strings; TIME remains text; DATETIME has no timezone; TIMESTAMP is UTC.",
                "Defaults, auto-increment, ON UPDATE, indexes and MySQL table options are not applied; reused constraint names remain only in the report.",
                "Counts prove conversion of this file, not completeness or consistency of its source snapshot.",
            ],
        }


def signature(source):
    info = os.fstat(source.fileno())
    return (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, info.st_ctime_ns)


def convert(source_path, flavor, destination, dry_run=False):
    require(flavor in FLAVORS, "unsupported_source_declaration")
    source_path, destination = Path(source_path), Path(destination)
    require(not destination.exists() and not destination.is_symlink(), "destination_exists")
    flags = os.O_RDONLY | os.O_NONBLOCK | getattr(os, "O_NOFOLLOW", 0)
    descriptor = os.open(source_path, flags)
    with os.fdopen(descriptor, "rb") as source:
        info = os.fstat(source.fileno())
        require(stat.S_ISREG(info.st_mode) and 0 < info.st_size <= MAX_SOURCE, "invalid_source_file")
        before = signature(source)
        with tempfile.TemporaryDirectory(prefix=".mysql-", dir=destination.parent) as directory:
            stage = Path(directory)
            adapter = Adapter(source, stage, flavor)
            report = adapter.parse()
            require(signature(source) == before, "source_changed")
            source.seek(0)
            digest = hashlib.file_digest(source, "sha256").hexdigest()
            require(signature(source) == before and digest == adapter.lexer.digest.hexdigest(), "source_changed")
            report["sourceBytes"] = info.st_size
            report["sourceSha256"] = digest
            output = stage / "import.sql"
            with output.open("rb") as artifact:
                report["outputSha256"] = hashlib.file_digest(artifact, "sha256").hexdigest()
            report["outputBytes"] = output.stat().st_size
            report_path = stage / "report.json"
            with report_path.open("w", encoding="utf-8", newline="\n") as report_file:
                json.dump(report, report_file, ensure_ascii=False, indent=2)
                report_file.write("\n")
            require(report_path.stat().st_size <= MAX_REPORT, "report_too_large")
            output.chmod(0o600)
            report_path.chmod(0o600)
            if dry_run:
                os.link(report_path, destination)
            else:
                destination.mkdir(mode=0o700)
                published = []
                try:
                    for name in ("import.sql", "report.json"):
                        os.link(stage / name, destination / name)
                        published.append(destination / name)
                except BaseException:
                    for path in published:
                        path.unlink()
                    destination.rmdir()
                    raise
            return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("dry-run", "convert"))
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path, help="new report file for dry-run; new directory for convert")
    parser.add_argument("--source-version", required=True, choices=FLAVORS, help="declared exporter family, not autodetected")
    args = parser.parse_args()
    try:
        report = convert(args.source, args.source_version, args.destination, args.action == "dry-run")
    except ConversionError as error:
        print(f"conversion failed [{error.code}] at line {error.line}", file=sys.stderr)
        return 1
    except (OSError, UnicodeError, sqlite3.Error):
        print("conversion failed [file_encoding_or_io_error]", file=sys.stderr)
        return 1
    print(f"validated {report['tableCount']} tables, {report['rowCount']} rows; {report['outputBytes']} output bytes")
    return 0


if __name__ == "__main__":
    sys.exit(main())
