package home

import (
	"fmt"
	"net/http"
	"net/http/httptest"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

var outage = fmt.Errorf("load: %w", core.ErrUnavailable)

func TestWriteErrorReportsStoreOutageAsUnavailable(t *testing.T) {
	response := httptest.NewRecorder()
	writeError(response, outage)
	if response.Code != http.StatusServiceUnavailable {
		t.Fatalf("status = %d, want 503", response.Code)
	}
}
