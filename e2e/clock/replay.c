#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/timex.h>
#include <time.h>
#include <unistd.h>

_Static_assert(sizeof(struct timex) == 208, "timex size");
_Static_assert(offsetof(struct timex, maxerror) == 24, "timex maxerror");
_Static_assert(offsetof(struct timex, esterror) == 32, "timex esterror");
_Static_assert(offsetof(struct timex, status) == 40, "timex status");

static void replay(long *offset, int *status, long *error)
{
    const char *path = getenv("E2E_CLOCK_REPLAY");
    char data[128];
    int file = path ? open(path, O_RDONLY) : -1;
    ssize_t count = file < 0 ? -1 : read(file, data, sizeof(data) - 1);
    if (file >= 0) close(file);
    if (count <= 0) abort();
    data[count] = 0;
    if (sscanf(data, "%ld %d %ld", offset, status, error) != 3) abort();
}

int clock_gettime(clockid_t id, struct timespec *value)
{
    int (*original)(clockid_t, struct timespec *) = dlsym(RTLD_NEXT, "clock_gettime");
    int result = original(id, value);
    if (result == 0 && id == CLOCK_REALTIME) {
        long offset, error;
        int status;
        replay(&offset, &status, &error);
        value->tv_sec += offset / 1000;
        value->tv_nsec += (offset % 1000) * 1000000;
        if (value->tv_nsec >= 1000000000) { value->tv_sec++; value->tv_nsec -= 1000000000; }
        if (value->tv_nsec < 0) { value->tv_sec--; value->tv_nsec += 1000000000; }
    }
    return result;
}

int adjtimex(struct timex *value)
{
    if (value->modes != 0) { errno = EPERM; return -1; }
    long offset, error;
    int status;
    replay(&offset, &status, &error);
    value->status = status;
    value->maxerror = error;
    value->esterror = error;
    return (status & STA_UNSYNC) ? TIME_ERROR : TIME_OK;
}
