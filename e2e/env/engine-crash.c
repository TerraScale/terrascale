#define _GNU_SOURCE
#include <dlfcn.h>
#include <stdlib.h>
#include <signal.h>
#include <string.h>
static void stopped(const char *operation, const char *path, int result) {
    const char *point = getenv("ENGINE_CRASH_POINT");
    if (result || !point || strcmp(point, operation)) return;
    const char *name = strrchr(path, '/');
    if (!name || name[1] == '_' || name[1] == '.') return;
    const char *suffix = !strcmp(operation, "publish") ? ".db.key" : ".db";
    size_t n = strlen(path), s = strlen(suffix);
    if (n >= s && !strcmp(path + n - s, suffix)) raise(SIGSTOP);
}
int rename(const char *from, const char *to) {
    int (*real)(const char *, const char *) = dlsym(RTLD_NEXT, "rename");
    int result = real(from, to);
    stopped("publish", to, result);
    return result;
}
int link(const char *from, const char *to) {
    int (*real)(const char *, const char *) = dlsym(RTLD_NEXT, "link");
    int result = real(from, to);
    stopped("publish", to, result);
    return result;
}
int unlink(const char *path) {
    int (*real)(const char *) = dlsym(RTLD_NEXT, "unlink");
    int result = real(path);
    stopped("delete", path, result);
    return result;
}
