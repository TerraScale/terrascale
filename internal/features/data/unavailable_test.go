package data

import (
	"fmt"
	"net/http"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

func TestFailureReportsStoreOutageAsUnavailable(t *testing.T) {
	if status, message := failure(fmt.Errorf("save: %w", core.ErrUnavailable), "Save query"); status != http.StatusServiceUnavailable {
		t.Fatalf("failure = %d %q, want 503", status, message)
	}
}
