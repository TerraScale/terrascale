#define _GNU_SOURCE
#include <dlfcn.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

struct source { void *handle; char *path; struct source *next; };
static struct source *sources;
static pthread_mutex_t source_gate = PTHREAD_MUTEX_INITIALIZER;

static void remember(void *handle, const char *path) {
    struct source *source = malloc(sizeof(*source));
    if (!source) abort();
    source->handle = handle;
    source->path = strdup(path ? path : "");
    if (!source->path) abort();
    pthread_mutex_lock(&source_gate);
    source->next = sources;
    sources = source;
    pthread_mutex_unlock(&source_gate);
}

static void forget(void *handle) {
    pthread_mutex_lock(&source_gate);
    struct source **current = &sources;
    while (*current && (*current)->handle != handle) current = &(*current)->next;
    if (*current) {
        struct source *removed = *current;
        *current = removed->next;
        free(removed->path);
        free(removed);
    }
    pthread_mutex_unlock(&source_gate);
}

static int targets(void *handle, const char *target) {
    size_t length = strlen(target);
    int matches = 0;
    pthread_mutex_lock(&source_gate);
    for (struct source *source = sources; source; source = source->next) {
        size_t size = strlen(source->path);
        if (source->handle == handle && size >= length && !strcmp(source->path + size - length, target)) {
            matches = 1;
            break;
        }
    }
    pthread_mutex_unlock(&source_gate);
    return matches;
}

static int injected(void *handle, const char *sql) {
    const char *control = getenv("ENGINE_INTEGRITY_CONTROL");
    if (!control || !sql) return 0;
    FILE *file = fopen(control, "r");
    if (!file) return 0;
    char kind[16] = {0};
    char target[4096] = {0};
    int code = 0;
    int parsed = fscanf(file, "%15s %d %4095s", kind, &code, target);
    fclose(file);
    if (parsed != 3 || (code != 10 && code != 11 && code != 26)) return 0;
    int opening = !strcmp(kind, "open") && strstr(sql, "PRAGMA cipher");
    int committing = !strcmp(kind, "commit") && !strcmp(sql, "COMMIT;");
    if ((!opening && !committing) || !targets(handle, target) || (committing && remove(control))) return 0;
    fprintf(stderr, "injected %s error %d\n", kind, code);
    fflush(stderr);
    return code;
}

int sqlite3_open_v2(const char *filename, void **database, int flags, const char *vfs) {
    int (*real)(const char *, void **, int, const char *) = dlsym(RTLD_NEXT, "sqlite3_open_v2");
    int result = real(filename, database, flags, vfs);
    if (*database) remember(*database, filename);
    return result;
}

int sqlite3_close_v2(void *database) {
    int (*real)(void *) = dlsym(RTLD_NEXT, "sqlite3_close_v2");
    forget(database);
    return real(database);
}

int sqlite3_exec(void *database, const char *sql, void *callback, void *argument, char **error) {
    int (*real)(void *, const char *, void *, void *, char **) = dlsym(RTLD_NEXT, "sqlite3_exec");
    int code = injected(database, sql);
    if (code) {
        if (error) *error = NULL;
        return code;
    }
    return real(database, sql, callback, argument, error);
}
