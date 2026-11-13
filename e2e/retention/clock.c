#define _GNU_SOURCE
#include <fcntl.h>
#include <stdlib.h>
#include <sys/syscall.h>
#include <sys/time.h>
#include <time.h>
#include <unistd.h>

static time_t offset(void)
{
    const char *path = getenv("E2E_RETENTION_CLOCK_FILE");
    if (path == NULL) abort();
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0) abort();
    char buffer[64];
    ssize_t count = read(fd, buffer, sizeof(buffer) - 1);
    close(fd);
    if (count <= 0) abort();
    buffer[count] = 0;
    char *end;
    long long seconds = strtoll(buffer, &end, 10);
    if (end == buffer || (*end != 0 && *end != '\n')) abort();
    return (time_t)seconds;
}

int clock_gettime(clockid_t id, struct timespec *value)
{
    int result = (int)syscall(SYS_clock_gettime, id, value);
    if (result == 0 && (id == CLOCK_REALTIME || id == CLOCK_REALTIME_COARSE))
        value->tv_sec += offset();
    return result;
}

int gettimeofday(struct timeval *restrict value, void *restrict zone)
{
    int result = (int)syscall(SYS_gettimeofday, value, zone);
    if (result == 0) value->tv_sec += offset();
    return result;
}

time_t time(time_t *value)
{
    struct timespec now;
    if (clock_gettime(CLOCK_REALTIME, &now) != 0) return (time_t)-1;
    if (value != NULL) *value = now.tv_sec;
    return now.tv_sec;
}
