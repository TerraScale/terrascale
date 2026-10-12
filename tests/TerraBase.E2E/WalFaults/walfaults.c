// Test-only faults on write-ahead log syncs, loaded into a node process with LD_PRELOAD by WalSyncFaults.cs: production
// code knows nothing of it. Turso's native library syncs with glibc's fsync (nm -D libturso_sdk_kit.so, 0.8.2); fdatasync
// is covered too in case a later version uses it.
//
// Only files whose path ends in "-wal" are affected, and only while $WALFAULTS_DIR is set. $WALFAULTS_DIR/control, read
// at every such sync, holds one line "<mode> <argument> [path suffix]":
//   delay <ms>          every sync waits <ms> before it runs
//   fail <errno>        every sync fails with <errno> without running
//   kill-at-sync <n>    SIGKILL at the entry of the n-th sync since the control file was last replaced
// An absent file or any other mode only forwards. The optional suffix limits the mode to the logs whose path ends in it.
// After every successful sync, "<length> <path>" of the log is appended to $WALFAULTS_DIR/synced: a power loss keeps at
// most that much of it.
#define _GNU_SOURCE
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

static long syncs;
static ino_t armed;

static int ends_with(const char *text, size_t length, const char *suffix)
{
    size_t size = strlen(suffix);
    return length >= size && memcmp(text + length - size, suffix, size) == 0;
}

static int sync_with_faults(int fd, const char *name)
{
    int (*real)(int) = (int (*)(int))dlsym(RTLD_NEXT, name);
    const char *dir = getenv("WALFAULTS_DIR");
    char link[32], path[4096];
    snprintf(link, sizeof link, "/proc/self/fd/%d", fd);
    ssize_t length = dir ? readlink(link, path, sizeof path - 1) : -1;
    if (length < 0 || !ends_with(path, (size_t)length, "-wal"))
    {
        return real(fd);
    }

    path[length] = 0;
    char file[4096], mode[16] = "", suffix[4096] = "";
    long argument = 0;
    struct stat status;
    snprintf(file, sizeof file, "%s/control", dir);
    FILE *control = fopen(file, "re");
    if (control)
    {
        // Replaced, not rewritten (WalSyncFaults moves a new file in): a new inode restarts the count.
        if (fstat(fileno(control), &status) == 0 && status.st_ino != armed)
        {
            armed = status.st_ino;
            __atomic_store_n(&syncs, 0, __ATOMIC_SEQ_CST);
        }

        if (fscanf(control, "%15s %ld %4095s", mode, &argument, suffix) < 2)
        {
            mode[0] = 0;
        }

        fclose(control);
    }

    if (suffix[0] == 0 || ends_with(path, (size_t)length, suffix))
    {
        if (strcmp(mode, "kill-at-sync") == 0 && __atomic_add_fetch(&syncs, 1, __ATOMIC_SEQ_CST) == argument)
        {
            kill(getpid(), SIGKILL);
        }

        if (strcmp(mode, "delay") == 0)
        {
            struct timespec wait = {argument / 1000, argument % 1000 * 1000000};
            while (nanosleep(&wait, &wait) != 0 && errno == EINTR)
            {
            }
        }

        if (strcmp(mode, "fail") == 0)
        {
            errno = (int)argument;
            return -1;
        }
    }

    int result = real(fd);
    if (result == 0 && fstat(fd, &status) == 0)
    {
        int saved = errno;
        char line[4200];
        int size = snprintf(line, sizeof line, "%lld %s\n", (long long)status.st_size, path);
        snprintf(file, sizeof file, "%s/synced", dir);
        int synced = open(file, O_WRONLY | O_APPEND | O_CREAT | O_CLOEXEC, 0600);
        if (synced >= 0)
        {
            // One write of one line: O_APPEND keeps concurrent syncs' lines whole. A lost line only makes a simulated
            // power loss drop more.
            if (write(synced, line, (size_t)size) < 0)
            {
            }

            close(synced);
        }

        errno = saved;
    }

    return result;
}

int fsync(int fd)
{
    return sync_with_faults(fd, "fsync");
}

int fdatasync(int fd)
{
    return sync_with_faults(fd, "fdatasync");
}
