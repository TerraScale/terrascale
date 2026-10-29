#define _POSIX_C_SOURCE 200809L
#include "turso.h"
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

extern uint32_t turso_terrascale_wal_contract(void);
extern turso_status_code_t turso_terrascale_wal_manual(const turso_connection_t *, const char **);
extern turso_status_code_t turso_terrascale_wal_state(const turso_connection_t *, uint64_t *, uint32_t *, const char **);
extern turso_status_code_t turso_terrascale_wal_frame_pages(const turso_connection_t *, uint64_t, uint32_t, uint32_t *, uint32_t *, const char **);
extern turso_status_code_t turso_terrascale_snapshot_read_page(const turso_connection_t *, uint32_t, uint8_t *, size_t, int32_t *, const char **);
extern turso_status_code_t turso_terrascale_wal_checkpoint_truncate(const turso_connection_t *, uint64_t, int32_t *, const char **);
extern uint32_t turso_terrascale_page_size(const turso_connection_t *);
static const char *error;

static void expect(turso_status_code_t status, turso_status_code_t wanted) {
    if (status != wanted) {
        fprintf(stderr, "wal smoke: status %d, expected %d: %s\n", status, wanted, error ? error : "no detail");
        exit(1);
    }
    error = NULL;
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

static int64_t scalar(turso_connection_t *connection, const char *sql) {
    turso_statement_t *statement = NULL;
    expect(turso_connection_prepare_single(connection, sql, &statement, &error), TURSO_OK);
    expect(turso_statement_step(statement, &error), TURSO_ROW);
    int64_t value = turso_statement_row_value_int(statement, 0);
    expect(turso_statement_step(statement, &error), TURSO_DONE);
    turso_statement_deinit(statement);
    return value;
}

static const turso_database_t *open_encrypted(const char *path) {
    const turso_database_t *database = NULL;
    turso_database_config_t config = {
        .path = path, .vfs = "syscall", .experimental_features = "encryption",
        .encryption_cipher = "aegis256",
        .encryption_hexkey = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"
    };
    expect(turso_database_new(&config, &database, &error), TURSO_OK);
    expect(turso_database_open(database, &error), TURSO_OK);
    return database;
}

static turso_connection_t *connect_manual(const turso_database_t *database) {
    turso_connection_t *connection = NULL;
    expect(turso_database_connect(database, &connection, &error), TURSO_OK);
    expect(turso_terrascale_wal_manual(connection, &error), TURSO_OK);
    return connection;
}

static void disconnect(turso_connection_t *connection) {
    expect(turso_connection_close(connection, &error), TURSO_OK);
    turso_connection_deinit(connection);
}

static void state(turso_connection_t *connection, uint64_t *max_frame, uint32_t *checkpoint_seq) {
    expect(turso_terrascale_wal_state(connection, max_frame, checkpoint_seq, &error), TURSO_OK);
}

static void begin_snapshot(turso_connection_t *reader) {
    expect(run(reader, "BEGIN"), TURSO_DONE);
    (void)scalar(reader, "SELECT count(*) FROM sqlite_schema");
}

static int contains(const uint8_t *page, size_t length, const char *needle) {
    size_t n = strlen(needle);
    for (size_t i = 0; i + n <= length; i++) {
        if (memcmp(page + i, needle, n) == 0) return 1;
    }
    return 0;
}

static int read_contains(turso_connection_t *reader, uint32_t number, uint8_t *page, size_t length, const char *needle) {
    int32_t found = 0;
    expect(turso_terrascale_snapshot_read_page(reader, number, page, length, &found, &error), TURSO_OK);
    assert(found);
    return contains(page, length, needle);
}

static int marked_pages(turso_connection_t *reader, uint8_t *page, size_t length, const char *needle) {
    int64_t count = scalar(reader, "PRAGMA page_count");
    int marked = 0;
    for (int64_t number = 1; number <= count; number++) {
        marked += read_contains(reader, (uint32_t)number, page, length, needle);
    }
    return marked;
}

static void file_stamp(const char *path, struct stat *info) {
    assert(stat(path, info) == 0);
}

static int same_stamp(const struct stat *a, const struct stat *b) {
    return a->st_size == b->st_size && a->st_mtim.tv_sec == b->st_mtim.tv_sec && a->st_mtim.tv_nsec == b->st_mtim.tv_nsec;
}

int main(void) {
    assert(turso_terrascale_wal_contract() == 1);
    char directory[] = "/tmp/turso-wal-contract-XXXXXX";
    assert(mkdtemp(directory) != NULL);
    char path[256], wal[272];
    assert(snprintf(path, sizeof(path), "%s/test.db", directory) < (int)sizeof(path));
    assert(snprintf(wal, sizeof(wal), "%s-wal", path) < (int)sizeof(wal));
    const turso_database_t *database = open_encrypted(path);
    turso_connection_t *writer = connect_manual(database);
    turso_connection_t *reader = connect_manual(database);
    size_t length = turso_terrascale_page_size(writer);
    assert(length == 4096);
    uint8_t *page = malloc(length);
    assert(page != NULL);

    uint64_t max_frame = 0, first = 0;
    uint32_t seq = 0, first_seq = 0;
    expect(run(writer, "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT)"), TURSO_DONE);
    expect(run(writer, "INSERT INTO t VALUES(1, 'marker-before')"), TURSO_DONE);
    state(writer, &first, &first_seq);
    assert(first > 0 && first < 64);
    uint32_t pages[64], sizes[64];
    int32_t found = 0;
    expect(turso_terrascale_wal_frame_pages(reader, 1, 1, pages, sizes, &error), TURSO_MISUSE);
    expect(turso_terrascale_snapshot_read_page(reader, 1, page, length, &found, &error), TURSO_MISUSE);
    begin_snapshot(reader);
    expect(turso_terrascale_wal_frame_pages(reader, 1, (uint32_t)first, pages, sizes, &error), TURSO_OK);
    assert(sizes[first - 1] > 0);
    expect(turso_terrascale_wal_frame_pages(reader, 1, (uint32_t)first + 1, pages, sizes, &error), TURSO_MISUSE);
    expect(turso_terrascale_wal_frame_pages(reader, (1ULL << 61) + 1, 1, pages, sizes, &error), TURSO_MISUSE);
    expect(turso_terrascale_wal_frame_pages(reader, UINT64_MAX, 2, pages, sizes, &error), TURSO_MISUSE);
    expect(turso_terrascale_wal_frame_pages(reader, 1, 2, pages, pages + 1, &error), TURSO_MISUSE);
    uint32_t leaf = 0;
    for (uint64_t frame = first; frame > 0 && leaf == 0; frame--) {
        if (read_contains(reader, pages[frame - 1], page, length, "marker-before")) leaf = pages[frame - 1];
    }
    assert(leaf > 1);
    expect(turso_terrascale_snapshot_read_page(reader, leaf, page, length - 1, &found, &error), TURSO_MISUSE);

    expect(run(writer, "UPDATE t SET v = 'marker-after!' WHERE id = 1"), TURSO_DONE);
    expect(run(writer, "PRAGMA wal_checkpoint(PASSIVE)"), TURSO_DONE);
    assert(read_contains(reader, leaf, page, length, "marker-before"));
    assert(!contains(page, length, "marker-after!"));
    expect(run(reader, "ROLLBACK"), TURSO_DONE);
    begin_snapshot(reader);
    assert(read_contains(reader, leaf, page, length, "marker-after!"));
    expect(run(reader, "ROLLBACK"), TURSO_DONE);

    expect(run(writer, "BEGIN IMMEDIATE"), TURSO_DONE);
    expect(run(writer, "INSERT INTO t VALUES(0, 'uncommitted')"), TURSO_DONE);
    expect(turso_terrascale_wal_state(writer, &max_frame, &seq, &error), TURSO_MISUSE);
    expect(turso_terrascale_wal_frame_pages(writer, 1, 1, pages, sizes, &error), TURSO_MISUSE);
    expect(run(writer, "ROLLBACK"), TURSO_DONE);

    state(writer, &first, &first_seq);
    struct stat before, after;
    file_stamp(path, &before);
    for (int i = 0; i < 1500; i++) {
        char sql[128];
        assert(snprintf(sql, sizeof(sql), "INSERT INTO t VALUES(%d, 'row-%d')", i + 2, i) < (int)sizeof(sql));
        expect(run(writer, sql), TURSO_DONE);
    }
    state(writer, &max_frame, &seq);
    assert(max_frame >= first + 1500);
    assert(seq == first_seq);
    file_stamp(path, &after);
    assert(same_stamp(&before, &after));
    disconnect(reader);
    disconnect(writer);
    turso_database_deinit(database);
    file_stamp(path, &after);
    assert(same_stamp(&before, &after));
    file_stamp(wal, &after);
    assert(after.st_size > 0);

    database = open_encrypted(path);
    writer = connect_manual(database);
    reader = connect_manual(database);
    uint64_t reopened = 0;
    state(writer, &reopened, &seq);
    assert(reopened == max_frame);
    assert(scalar(writer, "SELECT count(*) FROM t") == 1501);
    int32_t truncated = -1;
    begin_snapshot(reader);
    expect(turso_terrascale_wal_checkpoint_truncate(writer, reopened, &truncated, &error), TURSO_OK);
    assert(truncated == 0);
    expect(turso_terrascale_wal_checkpoint_truncate(reader, reopened, &truncated, &error), TURSO_MISUSE);
    expect(run(reader, "ROLLBACK"), TURSO_DONE);
    state(writer, &max_frame, &seq);
    assert(max_frame == reopened);
    expect(turso_terrascale_wal_checkpoint_truncate(writer, reopened - 1, &truncated, &error), TURSO_OK);
    assert(truncated == 0);
    expect(turso_terrascale_wal_checkpoint_truncate(writer, reopened, &truncated, &error), TURSO_OK);
    assert(truncated == 1);
    state(writer, &max_frame, &seq);
    assert(max_frame == 0);
    begin_snapshot(reader);
    assert(marked_pages(reader, page, length, "marker-after!") == 1);
    expect(run(reader, "ROLLBACK"), TURSO_DONE);

    expect(run(writer, "INSERT INTO t VALUES(-1, 'after-truncate')"), TURSO_DONE);
    uint64_t committed = 0;
    uint32_t committed_seq = 0;
    state(writer, &committed, &committed_seq);
    assert(committed > 0);
    expect(run(reader, "PRAGMA wal_checkpoint(TRUNCATE)"), TURSO_DONE);
    state(writer, &max_frame, &seq);
    assert(max_frame == committed && seq == committed_seq);
    begin_snapshot(writer);
    state(writer, &max_frame, &seq);
    assert(max_frame == 0);
    expect(turso_terrascale_wal_frame_pages(writer, 1, 1, pages, sizes, &error), TURSO_MISUSE);
    expect(run(writer, "ROLLBACK"), TURSO_DONE);

    begin_snapshot(reader);
    expect(run(writer, "PRAGMA cache_size = 10"), TURSO_DONE);
    expect(run(writer, "BEGIN IMMEDIATE"), TURSO_DONE);
    for (int i = 0; i < 300; i++) {
        char sql[128];
        assert(snprintf(sql, sizeof(sql), "INSERT INTO t VALUES(%d, hex(randomblob(2000)))", 10000 + i) < (int)sizeof(sql));
        expect(run(writer, sql), TURSO_DONE);
    }
    state(reader, &max_frame, &seq);
    assert(max_frame == 0);
    expect(turso_terrascale_wal_frame_pages(reader, 1, 3, pages, sizes, &error), TURSO_MISUSE);
    expect(run(writer, "ROLLBACK"), TURSO_DONE);
    expect(run(reader, "ROLLBACK"), TURSO_DONE);
    disconnect(reader);
    disconnect(writer);
    turso_database_deinit(database);
    expect(turso_terrascale_wal_manual(NULL, &error), TURSO_MISUSE);

    free(page);
    assert(unlink(path) == 0);
    (void)unlink(wal);
    assert(rmdir(directory) == 0);
    printf("wal contract smoke: marker=1, %llu frames with the database file untouched across close, snapshot page reads decrypt under a passive checkpoint, write transactions, checkpoints and spills of other connections and bad ranges refused, busy or stale truncates kept, explicit TRUNCATE resets frames\n", (unsigned long long)reopened);
    return 0;
}
