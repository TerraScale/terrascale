package admin

import (
	"fmt"
	"net/http"
	"net/http/httptest"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

var outage = fmt.Errorf("load: %w", core.ErrUnavailable)

func TestAdminErrorsReportStoreOutageAsUnavailable(t *testing.T) {
	read := httptest.NewRecorder()
	writeAdminError(read, outage)
	write := httptest.NewRecorder()
	writeMutationError(write, httptest.NewRequest(http.MethodPost, "/admin", nil), "/admin", outage)
	if read.Code != http.StatusServiceUnavailable || write.Code != http.StatusServiceUnavailable {
		t.Fatalf("read = %d, write = %d, want 503", read.Code, write.Code)
	}
}
