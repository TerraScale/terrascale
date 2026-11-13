#define _GNU_SOURCE
#include <dlfcn.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>

static _Thread_local void *integrity;

unsigned turso_connection_prepare_single(void *connection, const char *sql, void **statement, void **error) {
    unsigned (*real)(void *, const char *, void **, void **) = dlsym(RTLD_NEXT, "turso_connection_prepare_single");
    unsigned result = real(connection, sql, statement, error);
    integrity = strstr(sql, "integrity_check") ? *statement : NULL;
    return result;
}

unsigned turso_connection_prepare_first(void *connection, const char *sql, void **statement, size_t *tail, void **error) {
    unsigned (*real)(void *, const char *, void **, size_t *, void **) = dlsym(RTLD_NEXT, "turso_connection_prepare_first");
    unsigned result = real(connection, sql, statement, tail, error);
    integrity = strstr(sql, "integrity_check") ? *statement : NULL;
    return result;
}

unsigned turso_statement_step(void *statement, void **error) {
    const char *control = getenv("ENGINE_INTEGRITY_CONTROL");
    if (statement == integrity && control) {
        FILE *file = fopen(control, "r");
        int code = 0;
        if (file) {
            (void)fscanf(file, "%d", &code);
            fclose(file);
        }
        if (code == 11 || code == 26) {
            *error = NULL;
            fprintf(stderr, "injected integrity exception %d\n", code);
            return code == 11 ? 133 : 132;
        }
    }
    unsigned (*real)(void *, void **) = dlsym(RTLD_NEXT, "turso_statement_step");
    return real(statement, error);
}
