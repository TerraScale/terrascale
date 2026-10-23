/* Checks the exact pinned C ABI on both Linux architectures before artifact publication. */
#include "turso.h"
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

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

static uint64_t callback_calls, charged_work, charged_bytes, work_limit, byte_limit, largest_byte_charge;
typedef struct { uint64_t bytes, before, call, work; } byte_charge;
static byte_charge largest_charge, second_largest_charge;
static int stop_requested, stop_on_bytes;

static int32_t fts_budget(uint64_t work, uint64_t bytes) {
    callback_calls++;
    charged_work += work;
    charged_bytes += bytes;
    if (bytes > largest_byte_charge) largest_byte_charge = bytes;
    byte_charge charge = {bytes, charged_bytes - bytes, callback_calls, work};
    if (bytes > largest_charge.bytes) {
        second_largest_charge = largest_charge;
        largest_charge = charge;
    } else if (bytes > second_largest_charge.bytes) {
        second_largest_charge = charge;
    }
    return stop_requested || (stop_on_bytes && bytes != 0) || charged_work > work_limit || charged_bytes > byte_limit;
}

static void reset_budget(turso_connection_t *connection) {
    callback_calls = charged_work = charged_bytes = 0;
    work_limit = byte_limit = UINT64_MAX;
    largest_byte_charge = 0;
    largest_charge = second_largest_charge = (byte_charge){0};
    stop_requested = stop_on_bytes = 0;
    expect(turso_connection_set_fts_progress_handler(connection, fts_budget, &error), TURSO_OK, "set FTS callback");
}

static void interrupted(turso_connection_t *connection, const char *sql) {
    turso_statement_t *statement = prepare(connection, sql);
    expect(turso_statement_step(statement, &error), TURSO_INTERRUPT, sql);
    assert(callback_calls != 0);
    expect(turso_statement_reset(statement, &error), TURSO_OK, "reset interrupted FTS");
    turso_statement_deinit(statement);
    expect(turso_connection_set_fts_progress_handler(connection, NULL, &error), TURSO_OK, "clear FTS callback");
}

static uint64_t measure_work(turso_connection_t *connection, const char *sql) {
    reset_budget(connection);
    turso_statement_t *statement = prepare(connection, sql);
    turso_status_code_t status;
    while ((status = turso_statement_step(statement, &error)) == TURSO_ROW) { }
    expect(status, TURSO_DONE, sql);
    turso_statement_deinit(statement);
    assert(charged_work > 64);
    return charged_work;
}

static void budget_smoke(turso_connection_t *connection) {
    execute(connection, "CREATE TABLE budget_docs(id INTEGER PRIMARY KEY, title TEXT)");
    execute(connection, "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<256) INSERT INTO budget_docs SELECT x, 'native search' FROM n");
    execute(connection, "CREATE INDEX budget_fts ON budget_docs USING fts(title)");
    const char *stream = "SELECT id, fts_score(title, 'native') FROM budget_docs WHERE fts_match(title, 'native') LIMIT 10001";
    const char *count = "SELECT count(*) FROM budget_docs WHERE fts_match(title, 'native')";
    const char *ordered = "SELECT id, fts_score(title, 'native') AS score FROM budget_docs WHERE fts_match(title, 'native') ORDER BY score DESC LIMIT 10";
    reset_budget(connection);
    turso_statement_t *statement = prepare(connection, stream);
    expect(turso_statement_step(statement, &error), TURSO_ROW, stream);
    assert(charged_work > 0 && charged_bytes > 0);
    /* Arm only after the first row: the native reader must poll while advancing postings. */
    uint64_t previous_calls = callback_calls;
    stop_requested = 1;
    expect(turso_statement_step(statement, &error), TURSO_INTERRUPT, "cancel streaming native reader");
    assert(callback_calls > previous_calls);
    expect(turso_statement_reset(statement, &error), TURSO_OK, "reset streaming cancellation");
    turso_statement_deinit(statement);
    reset_budget(connection);
    stop_on_bytes = 1;
    interrupted(connection, stream);
    uint64_t count_work = measure_work(connection, count);
    reset_budget(connection);
    work_limit = count_work - 64;
    interrupted(connection, count);
    uint64_t ordered_work = measure_work(connection, ordered);
    reset_budget(connection);
    work_limit = ordered_work - 64;
    interrupted(connection, ordered);
    reset_budget(connection);
    /* An already-expired request deadline uses the same callback, before any SQL row function. */
    stop_requested = 1;
    interrupted(connection, stream);
    statement = prepare(connection, count);
    expect(turso_statement_step(statement, &error), TURSO_ROW, count);
    assert(turso_statement_row_value_int(statement, 0) == 256);
    expect(turso_statement_step(statement, &error), TURSO_DONE, count);
    turso_statement_deinit(statement);
    puts("native smoke: loading bytes, count, ordered scoring, streaming cancellation and recovery passed");
}

static void expect_count(turso_connection_t *connection, const char *sql, int count) {
    turso_statement_t *statement = prepare(connection, sql);
    expect(turso_statement_step(statement, &error), TURSO_ROW, sql);
    assert(turso_statement_row_value_int(statement, 0) == count);
    expect(turso_statement_step(statement, &error), TURSO_DONE, sql);
    turso_statement_deinit(statement);
}

static void many_clause_admission_smoke(turso_connection_t *connection) {
    execute(connection, "CREATE TABLE tiny_docs(id INTEGER PRIMARY KEY, title TEXT)");
    execute(connection, "CREATE INDEX tiny_fts ON tiny_docs USING fts(title)");
    execute(connection, "INSERT INTO tiny_docs VALUES (1, 'zaa')");
    char terms[4096] = "";
    for (int i = 0; i < 500; i++) {
        char term[4] = {'z', (char)('a' + i / 26), (char)('a' + i % 26), '\0'};
        if (i) strcat(terms, " OR ");
        strcat(terms, term);
    }
    assert(strlen(terms) == 3496);
    const char *sql = "SELECT id, fts_score(title, ?1) FROM tiny_docs WHERE fts_match(title, ?1) LIMIT 10001";
    /* Measure setup separately: loading/scratch can exceed a fixed small byte ceiling. */
    reset_budget(connection);
    turso_statement_t *statement = prepare(connection, sql);
    expect(turso_statement_bind_positional_text(statement, 1, terms, strlen(terms)), TURSO_OK, "bind healthy many clauses");
    expect(turso_statement_step(statement, &error), TURSO_ROW, sql);
    assert(turso_statement_row_value_int(statement, 0) == 1);
    assert(turso_statement_row_value_double(statement, 1) > 0);
    expect(turso_statement_step(statement, &error), TURSO_DONE, sql);
    turso_statement_deinit(statement);
    byte_charge weight_charge = second_largest_charge;
    byte_charge scorer_charge = largest_charge;
    assert(weight_charge.bytes >= (uint64_t)500 * 4 * 1024);
    assert(scorer_charge.bytes >= (uint64_t)500 * 64 * 1024);
    assert(weight_charge.work == 1 && scorer_charge.work >= 500);
    assert(weight_charge.call < scorer_charge.call);
    assert(weight_charge.before + weight_charge.bytes <= scorer_charge.before);
    for (int scorer = 0; scorer < 2; scorer++) {
        byte_charge target = scorer ? scorer_charge : weight_charge;
        reset_budget(connection);
        byte_limit = target.before;
        statement = prepare(connection, sql);
        expect(turso_statement_bind_positional_text(statement, 1, terms, strlen(terms)), TURSO_OK, "bind 500 distinct clauses");
        turso_status_code_t status = turso_statement_step(statement, &error);
        printf("native smoke: %s refusal status=%d call=%llu prior=%llu charge=%llu limit=%llu work=%llu\n", scorer ? "scorer" : "weight", status,
               (unsigned long long)callback_calls, (unsigned long long)largest_charge.before,
               (unsigned long long)largest_byte_charge, (unsigned long long)byte_limit, (unsigned long long)largest_charge.work);
        fflush(stdout);
        expect(status, TURSO_INTERRUPT, "many clauses exceed native admission");
        assert(callback_calls == target.call);
        assert(largest_byte_charge == target.bytes);
        assert(charged_bytes == target.before + target.bytes);
        assert(largest_byte_charge >= (uint64_t)500 * (scorer ? 64 * 1024 : 4 * 1024));
        expect(turso_statement_reset(statement, &error), TURSO_OK, "reset clause admission refusal");
        turso_statement_deinit(statement);
    }
    expect(turso_connection_set_fts_progress_handler(connection, NULL, &error), TURSO_OK, "clear clause admission");
    statement = prepare(connection, sql);
    expect(turso_statement_bind_positional_text(statement, 1, terms, strlen(terms)), TURSO_OK, "bind healthy many clauses");
    expect(turso_statement_step(statement, &error), TURSO_ROW, sql);
    assert(turso_statement_row_value_int(statement, 0) == 1);
    assert(turso_statement_row_value_double(statement, 1) > 0);
    expect(turso_statement_step(statement, &error), TURSO_DONE, sql);
    turso_statement_deinit(statement);
    puts("native smoke: tiny index refuses unadmitted term weights and per-segment scorers");

    strcpy(terms, "zaa");
    for (int i = 1; i < 500; i++) strcat(terms, " OR zaa");
    reset_budget(connection);
    byte_limit = 128 * 1024 * 1024;
    search(connection, sql, terms, 1);
    assert(largest_byte_charge < (uint64_t)500 * 4 * 1024);
    expect(turso_connection_set_fts_progress_handler(connection, NULL, &error), TURSO_OK, "clear optimized repetition budget");
    puts("native smoke: identical OR terms preserve matches and collapse before physical allocation");

    /* Distinct attachments remain in Turso's transaction registry between these completed statements. */
    execute(connection, "BEGIN");
    execute(connection, "UPDATE tiny_docs SET title = 'zaa updated' WHERE id = 1");
    expect_count(connection, "SELECT count(*) FROM tiny_docs WHERE fts_match(title, 'updated')", 1);
    expect_count(connection, "SELECT count(*) FROM budget_docs WHERE fts_match(title, 'native')", 256);
    expect_count(connection, "SELECT count(*) FROM tiny_docs WHERE fts_match(title, 'zaa')", 1);
    execute(connection, "ROLLBACK");
    expect_count(connection, "SELECT count(*) FROM tiny_docs WHERE fts_match(title, 'updated')", 0);
    expect_count(connection, "SELECT count(*) FROM tiny_docs WHERE fts_match(title, 'zaa')", 1);
    puts("native smoke: completed reads across indexes in an open transaction preserve rollback");
}

static void dense_phrase_admission_smoke(turso_connection_t *connection) {
    const size_t tokens = 262144;
    char *body = malloc(tokens * 2 + 1);
    assert(body != NULL);
    for (size_t i = 0; i < tokens; i++) { body[i * 2] = 'a'; body[i * 2 + 1] = ' '; }
    body[tokens * 2] = '\0';
    execute(connection, "CREATE TABLE dense_docs(id INTEGER PRIMARY KEY, title TEXT)");
    turso_statement_t *statement = prepare(connection, "INSERT INTO dense_docs VALUES (1, ?1)");
    expect(turso_statement_bind_positional_text(statement, 1, body, tokens * 2), TURSO_OK, "bind dense document");
    expect(turso_statement_step(statement, &error), TURSO_DONE, "insert dense document");
    turso_statement_deinit(statement);
    free(body);
    execute(connection, "CREATE INDEX dense_fts ON dense_docs USING fts(title)");
    const char *scored = "SELECT id, fts_score(title, ?1) FROM dense_docs WHERE fts_match(title, ?1) LIMIT 10001";
    const char *counted = "SELECT count(*) FROM dense_docs WHERE fts_match(title, ?1)";
    for (int variant = 0; variant < 5; variant++) {
        const int clauses = variant == 3 ? 20 : variant == 1 ? 100 : 300;
        char terms[4096] = "";
        for (int i = 0; i < clauses; i++) {
            char phrase[64];
            if (variant == 2) {
                snprintf(phrase, sizeof(phrase), "\"a %c%c%c\"*", 'a' + i / (26 * 26), 'a' + i / 26 % 26, 'a' + i % 26);
            } else if (variant == 3) {
                snprintf(phrase, sizeof(phrase), "\"a a\"^%d", i + 2);
            } else {
                snprintf(phrase, sizeof(phrase), "\"a a\"~%d", variant == 1 ? i + 20000 : i);
            }
            if (i) strcat(terms, " OR ");
            strcat(terms, phrase);
        }
        assert(strlen(terms) < 4096);
        reset_budget(connection);
        byte_limit = 128 * 1024 * 1024;
        statement = prepare(connection, variant == 4 ? counted : scored);
        expect(turso_statement_bind_positional_text(statement, 1, terms, strlen(terms)), TURSO_OK, "bind dense phrases");
        turso_status_code_t status = turso_statement_step(statement, &error);
        uint64_t position_bound = (uint64_t)tokens * clauses * (variant == 2 ? 1 : 2) * 64;
        printf("native smoke: dense phrase variant=%d status=%d prior=%llu charge=%llu limit=%llu positional-bound=%llu\n", variant, status,
               (unsigned long long)(charged_bytes - largest_byte_charge), (unsigned long long)largest_byte_charge,
               (unsigned long long)byte_limit, (unsigned long long)position_bound);
        fflush(stdout);
        expect(status, TURSO_INTERRUPT, "dense phrases refuse position admission");
        assert(largest_byte_charge >= position_bound);
        assert(charged_bytes - largest_byte_charge <= byte_limit);
        assert(largest_byte_charge > byte_limit);
        expect(turso_statement_reset(statement, &error), TURSO_OK, "reset dense phrase admission refusal");
        turso_statement_deinit(statement);
    }
    char optimized[4096] = "\"a a\"";
    for (int i = 1; i < 300; i++) strcat(optimized, " OR \"a a\"");
    assert(strlen(optimized) == 2696);
    reset_budget(connection);
    byte_limit = 128 * 1024 * 1024;
    search(connection, scored, optimized, 1);
    expect(turso_connection_set_fts_progress_handler(connection, NULL, &error), TURSO_OK, "clear dense phrase admission");
    search(connection, scored, "\"a a\"", 1);
    puts("native smoke: dense repeated/sloppy/prefix/boosted phrases and count refuse before scorer allocation; recovery passed");
}

static void carrying_slop_admission_smoke(turso_connection_t *connection) {
    char body[32770];
    for (size_t i = 0; i < 8192; i++) { body[i * 2] = 'b'; body[i * 2 + 1] = ' '; }
    for (size_t i = 8192; i < 16384; i++) { body[i * 2] = 'a'; body[i * 2 + 1] = ' '; }
    body[32768] = 'c';
    body[32769] = '\0';
    execute(connection, "CREATE TABLE slop_docs(id INTEGER PRIMARY KEY, title TEXT)");
    turso_statement_t *statement = prepare(connection, "INSERT INTO slop_docs VALUES (1, ?1)");
    expect(turso_statement_bind_positional_text(statement, 1, body, strlen(body)), TURSO_OK, "bind separated token blocks");
    expect(turso_statement_step(statement, &error), TURSO_DONE, "insert separated token blocks");
    turso_statement_deinit(statement);
    execute(connection, "CREATE INDEX slop_fts ON slop_docs USING fts(title)");
    const char *terms = "\"a b c\"~20000";
    assert(strlen(terms) == 13);
    const char *queries[] = {
        "SELECT id, fts_score(title, ?1) FROM slop_docs WHERE fts_match(title, ?1) LIMIT 10001",
        "SELECT count(*) FROM slop_docs WHERE fts_match(title, ?1)",
        "SELECT id, fts_score(title, ?1) AS score FROM slop_docs WHERE fts_match(title, ?1) ORDER BY score DESC LIMIT 10"
    };
    uint64_t tokens = 16385;
    uint64_t carrying_bound = (tokens * tokens + 2 * tokens) * 64;
    for (size_t i = 0; i < sizeof(queries) / sizeof(queries[0]); i++) {
        reset_budget(connection);
        byte_limit = 128 * 1024 * 1024;
        statement = prepare(connection, queries[i]);
        expect(turso_statement_bind_positional_text(statement, 1, terms, strlen(terms)), TURSO_OK, "bind carrying slop");
        turso_status_code_t status = turso_statement_step(statement, &error);
        printf("native smoke: carrying slop variant=%zu status=%d prior=%llu charge=%llu bound=%llu\n", i, status,
               (unsigned long long)(charged_bytes - largest_byte_charge), (unsigned long long)largest_byte_charge,
               (unsigned long long)carrying_bound);
        fflush(stdout);
        expect(status, TURSO_INTERRUPT, "refuse carrying-slop scratch before scorer construction");
        assert(largest_byte_charge >= carrying_bound);
        assert(charged_bytes - largest_byte_charge <= byte_limit);
        assert(largest_byte_charge > byte_limit);
        expect(turso_statement_reset(statement, &error), TURSO_OK, "reset carrying-slop refusal");
        turso_statement_deinit(statement);
    }
    expect(turso_connection_set_fts_progress_handler(connection, NULL, &error), TURSO_OK, "clear carrying-slop admission");
    execute(connection, "CREATE TABLE small_slop_docs(id INTEGER PRIMARY KEY, title TEXT)");
    execute(connection, "INSERT INTO small_slop_docs VALUES (1, 'a b c')");
    execute(connection, "CREATE INDEX small_slop_fts ON small_slop_docs USING fts(title)");
    reset_budget(connection);
    byte_limit = 128 * 1024 * 1024;
    search(connection, "SELECT id, fts_score(title, ?1) FROM small_slop_docs WHERE fts_match(title, ?1) LIMIT 10001", terms, 1);
    expect_count(connection, "SELECT count(*) FROM small_slop_docs WHERE fts_match(title, '\"a b c\"~20000')", 1);
    expect(turso_connection_set_fts_progress_handler(connection, NULL, &error), TURSO_OK, "clear healthy carrying-slop admission");
    puts("native smoke: three-term carrying-slop reader/count/ordered queries refuse scratch; small admitted slop succeeds");
}

static void candidate_seek_smoke(turso_connection_t *connection) {
    execute(connection, "CREATE TABLE collection(id TEXT NOT NULL UNIQUE, doc TEXT)");
    execute(connection, "INSERT INTO collection VALUES ('one', '{\"body\":\"native\"}'), ('two', '{}')");
    const char *from = " FROM (SELECT value AS hit_id FROM json_each(?1)) AS hits CROSS JOIN collection ON collection.id = hits.hit_id";
    char sql[1024];
    snprintf(sql, sizeof(sql), "EXPLAIN QUERY PLAN SELECT collection.id, collection.doc%s", from);
    turso_statement_t *statement = prepare(connection, sql);
    expect(turso_statement_bind_positional_text(statement, 1, "[\"one\"]", 7), TURSO_OK, "bind candidate ids");
    int seeks = 0;
    turso_status_code_t status;
    while ((status = turso_statement_step(statement, &error)) == TURSO_ROW) {
        int64_t length = turso_statement_row_value_bytes_count(statement, 3);
        const char *text = turso_statement_row_value_bytes_ptr(statement, 3);
        char detail[1024];
        assert(length >= 0 && (size_t)length < sizeof(detail));
        memcpy(detail, text, (size_t)length);
        detail[length] = '\0';
        printf("candidate plan: %s\n", detail);
        assert(strstr(detail, "SCAN collection") == NULL);
        seeks += strstr(detail, "SEARCH collection USING") != NULL;
    }
    expect(status, TURSO_DONE, sql);
    assert(seeks == 1);
    turso_statement_deinit(statement);
    snprintf(sql, sizeof(sql), "SELECT collection.id%s", from);
    statement = prepare(connection, sql);
    expect(turso_statement_bind_positional_text(statement, 1, "[\"one\"]", 7), TURSO_OK, "bind candidate ids");
    expect(turso_statement_step(statement, &error), TURSO_ROW, sql);
    assert(turso_statement_row_value_bytes_count(statement, 0) == 3);
    assert(memcmp(turso_statement_row_value_bytes_ptr(statement, 0), "one", 3) == 0);
    expect(turso_statement_step(statement, &error), TURSO_DONE, sql);
    turso_statement_deinit(statement);
    puts("native smoke: json_each candidate lookup uses indexed id seek");
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
        budget_smoke(connection);
        many_clause_admission_smoke(connection);
        dense_phrase_admission_smoke(connection);
        carrying_slop_admission_smoke(connection);
        candidate_seek_smoke(connection);
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
