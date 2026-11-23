package httpx

import (
	"context"
	"net/http"
	"strconv"
	"strings"
	"time"
)

func RetryAfter(value string, limit time.Duration) (time.Duration, bool) {
	value = strings.TrimSpace(value)
	if value == "" {
		return 0, false
	}
	seconds, err := strconv.ParseUint(value, 10, 64)
	if err == nil {
		if seconds > uint64(limit/time.Second) {
			return limit, true
		}
		return time.Duration(seconds) * time.Second, true
	}
	if strings.Trim(value, "0123456789") == "" {
		return limit, true
	}
	if deadline, err := http.ParseTime(value); err == nil {
		return max(time.Until(deadline), 0), true
	}
	return 0, false
}

func Wait(ctx context.Context, delay time.Duration) error {
	if delay <= 0 {
		return ctx.Err()
	}
	timer := time.NewTimer(delay)
	defer timer.Stop()
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-timer.C:
		return nil
	}
}
