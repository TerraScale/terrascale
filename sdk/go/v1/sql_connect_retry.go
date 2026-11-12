package v1

import (
	"context"
	"time"
)

const sqlConnectAttempts = 3
const sqlConnectDelayCap = 30 * time.Second

func waitSQLConnect(ctx context.Context, refusal *APIError, attempt int) (bool, error) {
	if err := ctx.Err(); err != nil {
		return false, err
	}
	if attempt >= sqlConnectAttempts || (refusal.Status != 429 && refusal.Status != 503) || !refusal.retryAfterSet {
		return false, nil
	}
	delay := min(refusal.RetryAfter, sqlConnectDelayCap)
	if deadline, ok := ctx.Deadline(); ok && delay >= time.Until(deadline) {
		return false, nil
	}
	timer := time.NewTimer(delay)
	defer timer.Stop()
	select {
	case <-ctx.Done():
		return false, ctx.Err()
	case <-timer.C:
		return true, nil
	}
}
