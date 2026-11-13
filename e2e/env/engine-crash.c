#define _GNU_SOURCE
#include <dlfcn.h>
#include <stdlib.h>
#include <signal.h>
#include <string.h>
#include <errno.h>
#include <stdio.h>
#include <unistd.h>
#include <fcntl.h>
#include <stdarg.h>
static void stopped(const char *operation, const char *path, int result) {
    const char *point = getenv("ENGINE_CRASH_POINT");
    if (result || !point || strcmp(point, operation)) return;
    const char *name = strrchr(path, '/');
    if (!name || name[1] == '_' || name[1] == '.') return;
    const char *suffix = !strcmp(operation, "publish") ? ".db.key" : !strcmp(operation, "deleting") ? ".db.deleting" : ".db";
    size_t n = strlen(path), s = strlen(suffix);
    if (n >= s && !strcmp(path + n - s, suffix)) raise(SIGSTOP);
}
int rename(const char *from, const char *to) {
    int (*real)(const char *, const char *) = dlsym(RTLD_NEXT, "rename");
    if (strstr(from, ".rewriting")) stopped("rewrite-intent", to, 0);
    int result = real(from, to);
    stopped(strstr(from, ".rewriting") ? "rewrite" : "publish", to, result);
    return result;
}
int link(const char *from, const char *to) {
    int (*real)(const char *, const char *) = dlsym(RTLD_NEXT, "link");
    if (strstr(from, ".rewriting")) stopped("rewrite-intent", to, 0);
    int result = real(from, to);
    stopped(strstr(from, ".rewriting") ? "rewrite" : "publish", to, result);
    return result;
}
int unlink(const char *path) {
    int (*real)(const char *) = dlsym(RTLD_NEXT, "unlink");
    int result = real(path);
    stopped("delete", path, result);
    stopped("deleting", path, result);
    return result;
}
static int proof_failure(const char *path) {
    const char *target = getenv("ENGINE_PROOF_TARGET");
    const char *control = getenv("ENGINE_PROOF_CONTROL");
    if (!target || !control || !strstr(path, target)) return 0;
    FILE *file = fopen(control, "r");
    if (!file) return 0;
    char kind[8] = {0};
    int parsed = fscanf(file, "%7s", kind);
    fclose(file);
    if (parsed != 1) return 0;
    fprintf(stderr, "proof tenant file read\n");
    fflush(stderr);
    if (!strcmp(kind, "hold") || !strcmp(kind, "keyhold")) {
        char release[4096];
        (void)snprintf(release, sizeof(release), "%s.release", control);
        fprintf(stderr, "proof file claim held\n");
        fflush(stderr);
        int fd = open(release, O_RDONLY);
        char signal;
        if (fd >= 0) {
            (void)read(fd, &signal, 1);
            close(fd);
        }
    }
    if (!strcmp(kind, "pass") || !strcmp(kind, "keyhold")) return 0;
    errno = !strcmp(kind, "locked") ? EWOULDBLOCK : EIO;
    return 1;
}
int open64(const char *path, int flags, ...) {
    int (*real)(const char *, int, ...) = dlsym(RTLD_NEXT, "open64");
    mode_t mode = 0;
    if (flags & O_CREAT) {
        va_list args;
        va_start(args, flags);
        mode = va_arg(args, mode_t);
        va_end(args);
    }
    if (!(flags & O_CREAT) && proof_failure(path)) return -1;
    return real(path, flags, mode);
}

static void checkpoint_held(int fd) {
    const char *target = getenv("ENGINE_CHECKPOINT_TARGET");
    const char *control = getenv("ENGINE_CHECKPOINT_CONTROL");
    if (!target || !control) return;
    char descriptor[64], path[4096];
    (void)snprintf(descriptor, sizeof(descriptor), "/proc/self/fd/%d", fd);
    ssize_t size = readlink(descriptor, path, sizeof(path) - 1);
    if (size < 0) return;
    path[size] = 0;
    size_t length = strlen(path);
    if (!strstr(path, target) || length < 3 || strcmp(path + length - 3, ".db")) return;
    int (*remove_control)(const char *) = dlsym(RTLD_NEXT, "unlink");
    if (remove_control(control)) return;
    char entered[4096], release[4096];
    (void)snprintf(entered, sizeof(entered), "%s.entered", control);
    (void)snprintf(release, sizeof(release), "%s.release", control);
    int marker = open(entered, O_CREAT | O_WRONLY, 0600);
    if (marker >= 0) close(marker);
    fprintf(stderr, "native checkpoint backfill held\n");
    fflush(stderr);
    int gate = open(release, O_RDONLY);
    char signal;
    if (gate >= 0) {
        (void)read(gate, &signal, 1);
        close(gate);
    }
}
ssize_t pwrite(int fd, const void *buffer, size_t count, off_t offset) {
    ssize_t (*real)(int, const void *, size_t, off_t) = dlsym(RTLD_NEXT, "pwrite");
    ssize_t result = real(fd, buffer, count, offset);
    if (result >= 0) checkpoint_held(fd);
    return result;
}
ssize_t pwrite64(int fd, const void *buffer, size_t count, off64_t offset) {
    ssize_t (*real)(int, const void *, size_t, off64_t) = dlsym(RTLD_NEXT, "pwrite64");
    ssize_t result = real(fd, buffer, count, offset);
    if (result >= 0) checkpoint_held(fd);
    return result;
}
