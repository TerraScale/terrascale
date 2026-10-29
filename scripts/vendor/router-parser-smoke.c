#include <stdio.h>
#include <string.h>
#include "ts_sql.h"

static int contains(const uint8_t *bytes, size_t length, const char *expected) {
    size_t count = strlen(expected);
    for (size_t i = 0; i + count <= length; i++) {
        if (memcmp(bytes + i, expected, count) == 0) return 1;
    }
    return 0;
}

int main(void) {
    const char *sql = "UPDATE plain SET n=n-1 WHERE id=?";
    uint8_t *facts = NULL;
    size_t length = 0;
    if (ts_sql_abi_version() != 1) return 1;
    if (ts_sql_next((const uint8_t *)sql, strlen(sql), 0, &facts, &length) != 0) return 2;
    int valid = contains(facts, length, "\"kind\":\"update\"")
        && contains(facts, length, "\"assignment_dependence\":\"relative_or_unknown\"")
        && contains(facts, length, "\"shape\":\"equality\"");
    ts_sql_free(facts, length);
    if (!valid) return 3;
    const uint8_t invalid[] = {255};
    if (ts_sql_next(invalid, sizeof invalid, 0, &facts, &length) != 5) return 4;
    sql = "SELECT 1;garbage";
    if (ts_sql_next((const uint8_t *)sql, strlen(sql), 0, &facts, &length) != 2) return 5;
    if (facts != NULL || length != 0) return 6;
    puts("parser ABI integration: relative SET, equality predicate, UTF-8 and invalid suffix passed");
    return 0;
}
