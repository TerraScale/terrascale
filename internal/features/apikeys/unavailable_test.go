package apikeys

import (
	"fmt"
	"net/http"
	"net/http/httptest"
	"net/url"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

func TestRedirectPageErrorNamesAStoreOutage(t *testing.T) {
	response := httptest.NewRecorder()
	redirectPageError(response, httptest.NewRequest(http.MethodPost, "/api-keys", nil), fmt.Errorf("revoke: %w", core.ErrUnavailable), "")
	location, err := url.Parse(response.Header().Get("Location"))
	if err != nil {
		t.Fatal(err)
	}
	if got := location.Query().Get("error"); got != "The credential service is unavailable right now. Try again in a moment." {
		t.Errorf("outage message = %q", got)
	}
}
