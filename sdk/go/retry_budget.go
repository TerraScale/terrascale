package sdk

import (
	"context"
	"time"
)

// RetryBudgetError refuses a retry whose server cooldown exceeds the operation
// deadline. Its API error and full cooldown remain available to the caller.
type RetryBudgetError struct {
	RetryAfter time.Duration
	API        *APIError
}

func (e *RetryBudgetError) Error() string {
	return "sdk: server cooldown exceeds remaining operation deadline"
}
func (e *RetryBudgetError) Unwrap() error { return e.API }
func waitRootRetry(ctx context.Context, delay time.Duration, api *APIError) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if deadline, ok := ctx.Deadline(); ok && delay >= time.Until(deadline) {
		return &RetryBudgetError{RetryAfter: delay, API: api}
	}
	return sleepContext(ctx, delay)
}
