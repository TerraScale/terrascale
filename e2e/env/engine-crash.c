#define _GNU_SOURCE
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

static int ends_with(const char *text, const char *suffix) {
    size_t n = strlen(text), s = strlen(suffix);
    return n >= s && !strcmp(text + n - s, suffix);
}

static int targeted(const char *point, const char *path) {
    const char *armed = getenv("ENGINE_CRASH_POINT");
    const char *target = getenv("ENGINE_CRASH_TARGET");
    if (!armed || strcmp(armed, point)) return 0;
    if (!target || !*target) return 1;
    char directory[160];
    (void)snprintf(directory, sizeof(directory), "/%s/", target);
    return strstr(path, directory) != NULL;
}

static void stop(const char *point, const char *path) {
    fprintf(stderr, "engine crash point %s reached at %s\n", point, path);
    fflush(stderr);
    raise(SIGSTOP);
}

static int descriptor_path(int fd, char *path, size_t size) {
    char descriptor[64];
    (void)snprintf(descriptor, sizeof(descriptor), "/proc/self/fd/%d", fd);
    ssize_t length = readlink(descriptor, path, size - 1);
    if (length < 0) return 0;
    path[length] = 0;
    return 1;
}

int fsync(int fd) {
    int (*real)(int) = dlsym(RTLD_NEXT, "fsync");
    int result = real(fd);
    char path[4096];
    if (!result && descriptor_path(fd, path, sizeof(path)) && ends_with(path, ".tblog") && targeted("log-ahead", path))
        stop("log-ahead", path);
    return result;
}

int rename(const char *from, const char *to) {
    int (*real)(const char *, const char *) = dlsym(RTLD_NEXT, "rename");
    int result = real(from, to);
    if (!result && ends_with(from, ".tblog") && ends_with(to, ".tbseg") && targeted("seal", to)) stop("seal", to);
    return result;
}

int unlink(const char *path) {
    int (*real)(const char *) = dlsym(RTLD_NEXT, "unlink");
    int result = real(path);
    if (!result && ends_with(path, ".tblog") && targeted("seal", path)) stop("seal", path);
    if (!result && !ends_with(path, "-wal") && !ends_with(path, "-shm") && targeted("delete", path)) stop("delete", path);
    return result;
}

static int proof_failure(const char *path) {
    const char *target = getenv("ENGINE_PROOF_TARGET");
    const char *control = getenv("ENGINE_PROOF_CONTROL");
    if (!target || !control || !ends_with(path, target)) return 0;
    FILE *file = fopen(control, "r");
    if (!file) return 0;
    char kind[8] = {0};
    int parsed = fscanf(file, "%7s", kind);
    fclose(file);
    if (parsed != 1) return 0;
    fprintf(stderr, "proof tenant file open\n");
    fflush(stderr);
    if (!strcmp(kind, "hold")) {
        char release[4096];
        (void)snprintf(release, sizeof(release), "%s.release", control);
        fprintf(stderr, "proof file open held\n");
        fflush(stderr);
        int fd = open(release, O_RDONLY);
        char signal;
        if (fd >= 0) {
            (void)read(fd, &signal, 1);
            close(fd);
        }
        return 0;
    }
    if (strcmp(kind, "io")) return 0;
    errno = EIO;
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
    if (proof_failure(path)) return -1;
    struct stat existing;
    int created = (flags & O_CREAT) && ends_with(path, ".db") && !strstr(path, "/snapshots/") && stat(path, &existing) != 0;
    int result = real(path, flags, mode);
    if (result >= 0 && created && targeted("create", path)) stop("create", path);
    return result;
}

static void checkpoint_held(int fd) {
    const char *target = getenv("ENGINE_CHECKPOINT_TARGET");
    const char *control = getenv("ENGINE_CHECKPOINT_CONTROL");
    if (!target || !control) return;
    char path[4096];
    if (!descriptor_path(fd, path, sizeof(path)) || !ends_with(path, target)) return;
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

ssize_t pwrite64(int fd, const void *buffer, size_t count, off64_t offset) {
    ssize_t (*real)(int, const void *, size_t, off64_t) = dlsym(RTLD_NEXT, "pwrite64");
    ssize_t result = real(fd, buffer, count, offset);
    if (result >= 0) checkpoint_held(fd);
    return result;
}
