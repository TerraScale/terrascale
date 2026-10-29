#define _POSIX_C_SOURCE 200809L
#include "turso.h"
#include <assert.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

extern uint32_t turso_terrascale_storage_error_contract(void);
static const char *error;

static void expect(turso_status_code_t status, turso_status_code_t wanted) {
    if (status != wanted) {
        fprintf(stderr, "contract1 smoke: status %d, expected %d: %s\n", status, wanted, error ? error : "no detail");
        exit(1);
    }
}

static turso_status_code_t run(turso_connection_t *connection, const char *sql) {
    turso_statement_t *statement = NULL;
    turso_status_code_t status = turso_connection_prepare_single(connection, sql, &statement, &error);
    if (status == TURSO_OK) {
        while ((status = turso_statement_step(statement, &error)) == TURSO_ROW) { }
    }
    if (statement) turso_statement_deinit(statement);
    return status;
}

static turso_status_code_t open_encrypted(const char *path, const turso_database_t **database, turso_connection_t **connection) {
    turso_database_config_t config = {
        .path = path, .vfs = "syscall", .experimental_features = "encryption,index_method",
        .encryption_cipher = "aegis256",
        /* Synthetic test key; never a production credential. */
        .encryption_hexkey = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"
    };
    turso_status_code_t status = turso_database_new(&config, database, &error);
    if (status == TURSO_OK) status = turso_database_open(*database, &error);
    if (status == TURSO_OK) status = turso_database_connect(*database, connection, &error);
    return status;
}

static void close_database(const turso_database_t *database, turso_connection_t *connection) {
    expect(turso_connection_close(connection, &error), TURSO_OK);
    turso_connection_deinit(connection);
    turso_database_deinit(database);
}

static void check_search(turso_connection_t *connection) {
    turso_statement_t *statement = NULL;
    expect(turso_connection_prepare_single(connection,
        "SELECT id, fts_score(title, 'native') FROM docs WHERE fts_match(title, 'native')",
        &statement, &error), TURSO_OK);
    expect(turso_statement_step(statement, &error), TURSO_ROW);
    assert(turso_statement_row_value_int(statement, 0) == 1);
    assert(turso_statement_row_value_double(statement, 1) > 0);
    expect(turso_statement_step(statement, &error), TURSO_DONE);
    turso_statement_deinit(statement);
}

static const char *endless = "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x + 1 FROM n) SELECT sum(x) FROM n";

static void *interrupt_later(void *connection) {
    usleep(200000);
    expect(turso_connection_interrupt(connection, &error), TURSO_OK);
    return NULL;
}

static void check_bounds(turso_connection_t *connection) {
    expect(turso_connection_set_query_timeout_ms(connection, 200, &error), TURSO_OK);
    expect(run(connection, endless), TURSO_INTERRUPT);
    expect(run(connection, "SELECT 1"), TURSO_DONE);
    expect(turso_connection_set_query_timeout_ms(connection, 0, &error), TURSO_OK);
    pthread_t thread;
    assert(pthread_create(&thread, NULL, interrupt_later, connection) == 0);
    expect(run(connection, endless), TURSO_INTERRUPT);
    assert(pthread_join(thread, NULL) == 0);
    expect(run(connection, "SELECT 1"), TURSO_DONE);
    expect(turso_connection_set_max_value_length(connection, 1024, &error), TURSO_OK);
    expect(run(connection, "SELECT length(zeroblob(1024))"), TURSO_DONE);
    expect(run(connection, "SELECT zeroblob(536870912)"), TURSO_ERROR);
    expect(run(connection, "SELECT randomblob(536870912)"), TURSO_ERROR);
    expect(run(connection, "SELECT printf('%.*c', 4096, 'x')"), TURSO_ERROR);
    expect(run(connection, "SELECT x || x FROM (SELECT zeroblob(800) AS x)"), TURSO_ERROR);
    expect(turso_connection_set_max_value_length(connection, 8388608, &error), TURSO_OK);
    expect(run(connection, "SELECT length(zeroblob(1048576))"), TURSO_DONE);
}

int main(void) {
    assert(strcmp(turso_version(), "0.8.2") == 0);
    assert(turso_terrascale_storage_error_contract() == 1);
    char directory[] = "/tmp/turso-contract1-XXXXXX";
    assert(mkdtemp(directory) != NULL);
    char path[256];
    assert(snprintf(path, sizeof(path), "%s/test.db", directory) < (int)sizeof(path));
    const turso_database_t *database = NULL;
    turso_connection_t *connection = NULL;
    expect(open_encrypted(path, &database, &connection), TURSO_OK);
    expect(run(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, title TEXT)"), TURSO_DONE);
    expect(run(connection, "CREATE INDEX docs_fts ON docs USING fts(title)"), TURSO_DONE);
    expect(run(connection, "INSERT INTO docs VALUES(1, 'native encrypted search')"), TURSO_DONE);
    check_search(connection);
    check_bounds(connection);
    /* Same text as the typed native error must remain a constraint, never corruption. */
    expect(run(connection, "CREATE TRIGGER spoof BEFORE INSERT ON docs BEGIN SELECT RAISE(ABORT, 'Decryption failed for page=1'); END"), TURSO_DONE);
    expect(run(connection, "INSERT INTO docs VALUES(2, 'spoof')"), TURSO_CONSTRAINT);
    expect(run(connection, "PRAGMA wal_checkpoint(TRUNCATE)"), TURSO_DONE);
    close_database(database, connection);

    database = NULL; connection = NULL;
    expect(open_encrypted(path, &database, &connection), TURSO_OK);
    check_search(connection);
    close_database(database, connection);

    FILE *file = fopen(path, "r+b");
    assert(file != NULL);
    char header[5];
    assert(fread(header, 1, sizeof(header), file) == sizeof(header));
    assert(memcmp(header, "Turso", sizeof(header)) == 0);
    /* Alter authenticated page-1 ciphertext, leaving the clear header intact. */
    assert(fseek(file, 128, SEEK_SET) == 0);
    int byte = fgetc(file);
    assert(byte != EOF);
    assert(fseek(file, 128, SEEK_SET) == 0);
    assert(fputc(byte ^ 1, file) != EOF);
    assert(fclose(file) == 0);
    database = NULL; connection = NULL;
    turso_status_code_t status = open_encrypted(path, &database, &connection);
    if (status == TURSO_OK) status = run(connection, "SELECT count(*) FROM docs");
    expect(status, TURSO_CORRUPT);
    if (connection) turso_connection_deinit(connection);
    if (database) turso_database_deinit(database);
    assert(unlink(path) == 0);
    const char *suffixes[] = {"-wal", "-shm"};
    for (size_t i = 0; i < sizeof(suffixes) / sizeof(suffixes[0]); i++) {
        char sidecar[272];
        assert(snprintf(sidecar, sizeof(sidecar), "%s%s", path, suffixes[i]) < (int)sizeof(sidecar));
        (void)unlink(sidecar);
    }
    assert(rmdir(directory) == 0);
    puts("contract1 smoke: marker=1, aegis256 persisted FTS, SQL spoof constraint, interrupt, query timeout, value length and page tamper TURSO_CORRUPT passed");
    return 0;
}
