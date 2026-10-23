/* Checks the exact pinned C ABI on both Linux architectures before artifact publication. */
#include "turso.h"
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static const char *error;

static void expect(turso_status_code_t actual, turso_status_code_t wanted, const char *sql) {
    if (actual != wanted) {
        fprintf(stderr, "native smoke: status %d, expected %d: %s: %s\n", actual, wanted, sql, error ? error : "no detail");
        exit(1);
    }
}

static turso_statement_t *prepare(turso_connection_t *connection, const char *sql) {
    turso_statement_t *statement = NULL;
    expect(turso_connection_prepare_single(connection, sql, &statement, &error), TURSO_OK, sql);
    return statement;
}

static void execute(turso_connection_t *connection, const char *sql) {
    turso_statement_t *statement = prepare(connection, sql);
    expect(turso_statement_step(statement, &error), TURSO_DONE, sql);
    turso_statement_deinit(statement);
}

static void search(turso_connection_t *connection, const char *sql, const char *term, int found) {
    turso_statement_t *statement = prepare(connection, sql);
    expect(turso_statement_bind_positional_text(statement, 1, term, strlen(term)), TURSO_OK, "bind query");
    if (found) {
        expect(turso_statement_step(statement, &error), TURSO_ROW, sql);
        assert(turso_statement_row_value_int(statement, 0) == 1);
        assert(turso_statement_row_value_double(statement, 1) > 0);
    }
    expect(turso_statement_step(statement, &error), TURSO_DONE, sql);
    turso_statement_deinit(statement);
}

static int expression_execute(turso_connection_t *connection, const char *sql) {
    turso_statement_t *statement = NULL;
    turso_status_code_t status = turso_connection_prepare_single(connection, sql, &statement, &error);
    if (status == TURSO_OK) status = turso_statement_step(statement, &error);
    if (statement) turso_statement_deinit(statement);
    if (status == TURSO_DONE) return 1;
    printf("expression FTS unsupported: status %d: %s: %s\n", status, sql, error ? error : "no detail");
    return 0;
}

int main(int argc, char **argv) {
    (void)argv;
    assert(strcmp(turso_version(), "0.8.2") == 0);
    const turso_database_t *database = NULL;
    turso_connection_t *connection = NULL;
    turso_database_config_t config = {.path = ":memory:", .experimental_features = "index_method"};
    expect(turso_database_new(&config, &database, &error), TURSO_OK, "database_new");
    expect(turso_database_open(database, &error), TURSO_OK, "database_open");
    expect(turso_database_connect(database, &connection, &error), TURSO_OK, "database_connect");
    if (argc == 1) {
        execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, title TEXT)");
        execute(connection, "CREATE INDEX t_fts ON t USING fts(title)");
        execute(connection, "INSERT INTO t VALUES (1, 'native search'), (2, 'other')");
        const char *query = "SELECT id, fts_score(title, ?1) AS score FROM t WHERE fts_match(title, ?1) ORDER BY score DESC";
        search(connection, query, "native", 1);
        execute(connection, "BEGIN");
        execute(connection, "UPDATE t SET title = 'updated search' WHERE id = 1");
        search(connection, query, "native", 0);
        search(connection, query, "updated", 1);
        execute(connection, "ROLLBACK");
        search(connection, query, "native", 1);
        search(connection, query, "updated", 0);
        execute(connection, "BEGIN");
        execute(connection, "UPDATE t SET title = 'updated search' WHERE id = 1");
        execute(connection, "COMMIT");
        search(connection, query, "updated", 1);
        search(connection, query, "native", 0);
        execute(connection, "BEGIN");
        execute(connection, "DELETE FROM t WHERE id = 1");
        search(connection, query, "updated", 0);
        execute(connection, "ROLLBACK");
        search(connection, query, "updated", 1);
        execute(connection, "DELETE FROM t WHERE id = 1");
        search(connection, query, "updated", 0);
        execute(connection, "BEGIN");
        execute(connection, "INSERT INTO t VALUES (1, 'rolledback insert')");
        search(connection, query, "rolledback", 1);
        execute(connection, "ROLLBACK");
        search(connection, query, "rolledback", 0);
        puts("native smoke: column FTS match/score, commit, update, delete and rollback passed");
    } else {
        /* Expression support is diagnostic: the engine uses ordinary derived text columns. */
        execute(connection, "CREATE TABLE json_docs(id INTEGER PRIMARY KEY, doc TEXT)");
        if (expression_execute(connection, "CREATE INDEX json_fts ON json_docs USING fts(json_extract(doc,'$.body'))") &&
            expression_execute(connection, "INSERT INTO json_docs VALUES (1, '{\"body\":\"native search\"}'), (2, '{\"body\":\"other\"}')")) {
            const char *sql = "SELECT id, fts_score(json_extract(doc,'$.body'), 'native') AS score FROM json_docs WHERE fts_match(json_extract(doc,'$.body'), 'native') ORDER BY score DESC";
            turso_statement_t *statement = NULL;
            turso_status_code_t status = turso_connection_prepare_single(connection, sql, &statement, &error);
            if (status == TURSO_OK) status = turso_statement_step(statement, &error);
            if (status == TURSO_ROW) {
                assert(turso_statement_row_value_int(statement, 0) == 1);
                assert(turso_statement_row_value_double(statement, 1) > 0);
                puts("expression FTS CREATE INDEX + match + score supported");
            } else {
                printf("expression FTS search unsupported: status %d: %s\n", status, error ? error : "no detail");
            }
            if (statement) turso_statement_deinit(statement);
        }
    }
    expect(turso_connection_close(connection, &error), TURSO_OK, "connection_close");
    turso_connection_deinit(connection);
    turso_database_deinit(database);
    return 0;
}
