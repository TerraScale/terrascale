package v1

import (
	"context"
	"net/http"
	"time"

	"terrascale.tech/sdk/internal/httpx"
)

const sqlConnectAttempts = 3
const sqlConnectDelayCap = 30 * time.Second

func waitSQLConnect(ctx context.Context, refusal *APIError, attempt int) (bool, error) {
	if err := ctx.Err(); err != nil {
		return false, err
	}
	if attempt >= sqlConnectAttempts || (refusal.Status != http.StatusTooManyRequests && refusal.Status != http.StatusServiceUnavailable) || !refusal.retryAfterSet {
		return false, nil
	}
	delay := min(refusal.RetryAfter, sqlConnectDelayCap)
	if deadline, ok := ctx.Deadline(); ok && delay >= time.Until(deadline) {
		return false, nil
	}
	err := httpx.Wait(ctx, delay)
	return err == nil, err
}
