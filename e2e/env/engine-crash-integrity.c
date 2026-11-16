#define _GNU_SOURCE
#include <dlfcn.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <stdint.h>
#include <pthread.h>
#include <fcntl.h>
#include <unistd.h>

static _Thread_local void *integrity;

struct config_prefix { uint64_t async_io; const char *path; };
struct source { void *handle; char *path; struct source *next; };
static struct source *sources;
static pthread_mutex_t source_gate = PTHREAD_MUTEX_INITIALIZER;
static void *apply_statement;
static pthread_mutex_t apply_gate = PTHREAD_MUTEX_INITIALIZER;

static int apply_armed(void *connection, const char *sql) {
    const char *control = getenv("ENGINE_INTEGRITY_CONTROL");
    if (!control || !sql || !strstr(sql, "engine_retry")) return 0;
    FILE *file = fopen(control, "r");
    if (!file) return 0;
    char kind[16] = {0};
    char target[128] = {0};
    int parsed = fscanf(file, "%15s %127s", kind, target);
    fclose(file);
    if (parsed != 2 || strcmp(kind, "apply")) return 0;
    int matches = 0;
    pthread_mutex_lock(&source_gate);
    for (struct source *source = sources; source; source = source->next)
        if (source->handle == connection && strstr(source->path, target)) { matches = 1; break; }
    pthread_mutex_unlock(&source_gate);
    return matches;
}

static void *hold_statement;

static int hold_armed(void *connection, const char *sql) {
    const char *control = getenv("ENGINE_INTEGRITY_CONTROL");
    if (!control || !sql) return 0;
    FILE *file = fopen(control, "r");
    if (!file) return 0;
    char kind[16] = {0};
    char target[4096] = {0};
    char needle[256] = {0};
    int parsed = fscanf(file, "%15s %4095s %255[^\n]", kind, target, needle);
    fclose(file);
    if (parsed != 3 || strcmp(kind, "hold") || !strstr(sql, needle)) return 0;
    int matches = 0;
    pthread_mutex_lock(&source_gate);
    for (struct source *source = sources; source; source = source->next)
        if (source->handle == connection && strstr(source->path, target)) { matches = 1; break; }
    pthread_mutex_unlock(&source_gate);
    return matches;
}

static void remember_apply(void *connection, const char *sql, void *statement) {
    if (!statement) return;
    if (apply_armed(connection, sql)) {
        pthread_mutex_lock(&apply_gate);
        apply_statement = statement;
        pthread_mutex_unlock(&apply_gate);
    }
    if (hold_armed(connection, sql)) {
        pthread_mutex_lock(&apply_gate);
        hold_statement = statement;
        pthread_mutex_unlock(&apply_gate);
        const char *control = getenv("ENGINE_INTEGRITY_CONTROL");
        if (control) remove(control);
    }
}

static void hold_until_released(void *statement) {
    pthread_mutex_lock(&apply_gate);
    int hit = statement && statement == hold_statement;
    if (hit) hold_statement = NULL;
    pthread_mutex_unlock(&apply_gate);
    const char *control = getenv("ENGINE_INTEGRITY_CONTROL");
    if (!hit || !control) return;
    char path[4200];
    snprintf(path, sizeof path, "%s.release", control);
    int release = open(path, O_RDONLY);
    if (release < 0) abort();
    snprintf(path, sizeof path, "%s.held", control);
    FILE *held = fopen(path, "w");
    if (!held) abort();
    fclose(held);
    fprintf(stderr, "held statement until released\n");
    char byte;
    (void)read(release, &byte, 1);
    close(release);
    fprintf(stderr, "held statement released\n");
}

static int take_apply(void *statement) {
    pthread_mutex_lock(&apply_gate);
    int hit = statement == apply_statement && apply_statement;
    if (hit) apply_statement = NULL;
    pthread_mutex_unlock(&apply_gate);
    if (!hit) return 0;
    const char *control = getenv("ENGINE_INTEGRITY_CONTROL");
    if (control) remove(control);
    return 1;
}

static void remember(void *handle, const char *path) {
    struct source *source = malloc(sizeof(*source));
    if (!source) abort();
    source->handle = handle;
    source->path = strdup(path);
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

unsigned turso_database_new(void *config, void **database, void **error) {
    unsigned (*real)(void *, void **, void **) = dlsym(RTLD_NEXT, "turso_database_new");
    unsigned result = real(config, database, error);
    if (!result) remember(*database, ((struct config_prefix *)config)->path);
    return result;
}

unsigned turso_database_connect(void *database, void **connection, void **error) {
    unsigned (*real)(void *, void **, void **) = dlsym(RTLD_NEXT, "turso_database_connect");
    unsigned result = real(database, connection, error);
    if (!result) {
        char *path = NULL;
        pthread_mutex_lock(&source_gate);
        for (struct source *source = sources; source; source = source->next)
            if (source->handle == database) { path = strdup(source->path); break; }
        pthread_mutex_unlock(&source_gate);
        if (!path) abort();
        remember(*connection, path);
        free(path);
    }
    return result;
}

void turso_database_deinit(void *database) {
    void (*real)(void *) = dlsym(RTLD_NEXT, "turso_database_deinit");
    forget(database);
    real(database);
}

void turso_connection_deinit(void *connection) {
    void (*real)(void *) = dlsym(RTLD_NEXT, "turso_connection_deinit");
    forget(connection);
    real(connection);
}

static unsigned opening(void *connection, const char *sql, void **error) {
    const char *control = getenv("ENGINE_INTEGRITY_CONTROL");
    if (!control || !strstr(sql, "journal_mode")) return 0;
    FILE *file = fopen(control, "r");
    int code = 0;
    char target[4096] = {0};
    if (file) {
        (void)fscanf(file, "open%d %4095s", &code, target);
        fclose(file);
    }
    int matches = 0;
    pthread_mutex_lock(&source_gate);
    for (struct source *source = sources; source; source = source->next)
        if (source->handle == connection && target[0] && strstr(source->path, target)) { matches = 1; break; }
    pthread_mutex_unlock(&source_gate);
    if (!matches) return 0;
    if (code != 10 && code != 11 && code != 26) return 0;
    (void)remove(control);
    *error = NULL;
    fprintf(stderr, "injected first session exception %d\n", code);
    return code == 10 ? 134 : code == 11 ? 133 : 132;
}

unsigned turso_connection_prepare_single(void *connection, const char *sql, void **statement, void **error) {
    unsigned (*real)(void *, const char *, void **, void **) = dlsym(RTLD_NEXT, "turso_connection_prepare_single");
    unsigned injected = opening(connection, sql, error);
    if (injected) return injected;
    unsigned result = real(connection, sql, statement, error);
    integrity = strstr(sql, "integrity_check") ? *statement : NULL;
    if (!result) remember_apply(connection, sql, *statement);
    return result;
}

unsigned turso_connection_prepare_first(void *connection, const char *sql, void **statement, size_t *tail, void **error) {
    unsigned (*real)(void *, const char *, void **, size_t *, void **) = dlsym(RTLD_NEXT, "turso_connection_prepare_first");
    unsigned injected = opening(connection, sql, error);
    if (injected) return injected;
    unsigned result = real(connection, sql, statement, tail, error);
    integrity = strstr(sql, "integrity_check") ? *statement : NULL;
    if (!result) remember_apply(connection, sql, *statement);
    return result;
}

unsigned turso_statement_step(void *statement, void **error) {
    hold_until_released(statement);
    if (take_apply(statement)) {
        *error = NULL;
        fprintf(stderr, "injected apply engine failure\n");
        return 5;
    }
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
