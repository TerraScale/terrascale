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
extern turso_status_code_t turso_terrascale_wal_read_page(const turso_connection_t *, uint32_t, uint64_t, uint8_t *, size_t, int32_t *, const char **);
extern uint32_t turso_terrascale_page_size(const turso_connection_t *);
static const char *error;

static void expect(turso_status_code_t status, turso_status_code_t wanted) {
    if (status != wanted) {
        fprintf(stderr, "wal smoke: status %d, expected %d: %s\n", status, wanted, error ? error : "no detail");
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

static int64_t scalar(turso_connection_t *connection, const char *sql) {
    turso_statement_t *statement = NULL;
    expect(turso_connection_prepare_single(connection, sql, &statement, &error), TURSO_OK);
    expect(turso_statement_step(statement, &error), TURSO_ROW);
    int64_t value = turso_statement_row_value_int(statement, 0);
    expect(turso_statement_step(statement, &error), TURSO_DONE);
    turso_statement_deinit(statement);
    return value;
}

static void open_encrypted(const char *path, const turso_database_t **database, turso_connection_t **connection) {
    turso_database_config_t config = {
        .path = path, .vfs = "syscall", .experimental_features = "encryption",
        .encryption_cipher = "aegis256",
        .encryption_hexkey = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"
    };
    expect(turso_database_new(&config, database, &error), TURSO_OK);
    expect(turso_database_open(*database, &error), TURSO_OK);
    expect(turso_database_connect(*database, connection, &error), TURSO_OK);
    expect(turso_terrascale_wal_manual(*connection, &error), TURSO_OK);
}

static void close_database(const turso_database_t *database, turso_connection_t *connection) {
    expect(turso_connection_close(connection, &error), TURSO_OK);
    turso_connection_deinit(connection);
    turso_database_deinit(database);
}

static void state(turso_connection_t *connection, uint64_t *max_frame, uint32_t *checkpoint_seq) {
    expect(turso_terrascale_wal_state(connection, max_frame, checkpoint_seq, &error), TURSO_OK);
}

static int contains(const uint8_t *page, size_t length, const char *needle) {
    size_t n = strlen(needle);
    for (size_t i = 0; i + n <= length; i++) {
        if (memcmp(page + i, needle, n) == 0) return 1;
    }
    return 0;
}

static uint32_t page_with(turso_connection_t *connection, uint64_t watermark, const char *needle, uint8_t *page, size_t length) {
    uint32_t pages[4096], sizes[4096];
    assert(watermark <= 4096);
    expect(turso_terrascale_wal_frame_pages(connection, 1, (uint32_t)watermark, pages, sizes, &error), TURSO_OK);
    assert(sizes[watermark - 1] > 0);
    for (uint64_t frame = watermark; frame > 0; frame--) {
        int32_t found = 0;
        expect(turso_terrascale_wal_read_page(connection, pages[frame - 1], watermark, page, length, &found, &error), TURSO_OK);
        if (found && contains(page, length, needle)) return pages[frame - 1];
    }
    return 0;
}

static off_t file_size(const char *path) {
    struct stat info;
    return stat(path, &info) == 0 ? info.st_size : -1;
}

int main(void) {
    assert(turso_terrascale_wal_contract() == 1);
    char directory[] = "/tmp/turso-wal-contract-XXXXXX";
    assert(mkdtemp(directory) != NULL);
    char path[256], wal[272];
    assert(snprintf(path, sizeof(path), "%s/test.db", directory) < (int)sizeof(path));
    assert(snprintf(wal, sizeof(wal), "%s-wal", path) < (int)sizeof(wal));
    const turso_database_t *database = NULL;
    turso_connection_t *connection = NULL;
    open_encrypted(path, &database, &connection);
    size_t length = turso_terrascale_page_size(connection);
    assert(length == 4096);
    uint8_t *page = malloc(length);
    assert(page != NULL);

    uint64_t max_frame = 0, first = 0;
    uint32_t seq = 0, first_seq = 0;
    expect(run(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT)"), TURSO_DONE);
    expect(run(connection, "INSERT INTO t VALUES(1, 'marker-before')"), TURSO_DONE);
    state(connection, &first, &first_seq);
    assert(first > 0);
    uint32_t leaf = page_with(connection, first, "marker-before", page, length);
    assert(leaf > 1);

    expect(run(connection, "UPDATE t SET v = 'marker-after!' WHERE id = 1"), TURSO_DONE);
    state(connection, &max_frame, &seq);
    assert(max_frame > first);
    int32_t found = 0;
    expect(turso_terrascale_wal_read_page(connection, leaf, first, page, length, &found, &error), TURSO_OK);
    assert(found && contains(page, length, "marker-before") && !contains(page, length, "marker-after!"));
    expect(turso_terrascale_wal_read_page(connection, leaf, max_frame, page, length, &found, &error), TURSO_OK);
    assert(found && contains(page, length, "marker-after!"));
    expect(turso_terrascale_wal_read_page(connection, leaf, max_frame, page, length - 1, &found, &error), TURSO_MISUSE);

    for (int i = 0; i < 1500; i++) {
        char sql[128];
        assert(snprintf(sql, sizeof(sql), "INSERT INTO t VALUES(%d, 'row-%d')", i + 2, i) < (int)sizeof(sql));
        expect(run(connection, sql), TURSO_DONE);
    }
    state(connection, &max_frame, &seq);
    assert(max_frame > 1500);
    assert(seq == first_seq);
    close_database(database, connection);
    assert(file_size(wal) > 0);

    database = NULL; connection = NULL;
    open_encrypted(path, &database, &connection);
    uint64_t reopened = 0;
    state(connection, &reopened, &seq);
    assert(reopened == max_frame);
    assert(scalar(connection, "SELECT count(*) FROM t") == 1501);
    expect(run(connection, "PRAGMA wal_checkpoint(TRUNCATE)"), TURSO_DONE);
    state(connection, &max_frame, &seq);
    assert(max_frame == 0);
    expect(turso_terrascale_wal_read_page(connection, 1, 0, page, length, &found, &error), TURSO_OK);
    assert(found);
    int64_t page_count = scalar(connection, "PRAGMA page_count");
    int marked = 0;
    for (int64_t number = 1; number <= page_count; number++) {
        expect(turso_terrascale_wal_read_page(connection, (uint32_t)number, 0, page, length, &found, &error), TURSO_OK);
        assert(found);
        marked += contains(page, length, "marker-after!");
    }
    assert(marked == 1);
    close_database(database, connection);
    assert(file_size(wal) <= 0);

    free(page);
    assert(unlink(path) == 0);
    (void)unlink(wal);
    assert(rmdir(directory) == 0);
    expect(turso_terrascale_wal_manual(NULL, &error), TURSO_MISUSE);
    printf("wal contract smoke: marker=1, no automatic checkpoint over %llu frames, close keeps the WAL, watermark page reads decrypt, explicit TRUNCATE resets frames\n", (unsigned long long)reopened);
    return 0;
}
