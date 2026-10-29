package v1

import (
	"context"
	"time"
)

// RetryBudgetError refuses a retry whose server cooldown exceeds the remaining
// operation deadline. The original API error and full cooldown are preserved.
type RetryBudgetError struct {
	RetryAfter time.Duration
	API        *APIError
}

func (e *RetryBudgetError) Error() string {
	return "terrabase: server cooldown exceeds remaining operation deadline"
}
func (e *RetryBudgetError) Unwrap() error { return e.API }

func waitRetry(ctx context.Context, delay time.Duration, api *APIError) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if deadline, ok := ctx.Deadline(); ok && delay >= time.Until(deadline) {
		return &RetryBudgetError{RetryAfter: delay, API: api}
	}
	return sleep(ctx, delay)
}
