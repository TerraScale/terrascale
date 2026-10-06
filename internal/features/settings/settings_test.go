package settings

import (
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/backups"
)

func TestRetiredSettingsPageDoesNotHideMutationFailures(t *testing.T) {
	for _, tc := range []struct {
		name    string
		err     error
		status  int
		message string
	}{
		{"invalid input", core.Invalid("Retention must be from 1 to 90 days."), http.StatusBadRequest, "Retention must be from 1 to 90 days."},
		{"backups unavailable", backups.ErrUnavailable, http.StatusServiceUnavailable, msgBackupsUnavailable},
	} {
		t.Run(tc.name, func(t *testing.T) {
			w := httptest.NewRecorder()
			redirectOrError(w, httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/settings/audit-retention", nil), tc.err, "audit", "")
			if w.Code != tc.status || strings.TrimSpace(w.Body.String()) != tc.message || w.Header().Get("Location") != "" {
				t.Fatalf("response = %d %q, Location=%q", w.Code, w.Body.String(), w.Header().Get("Location"))
			}
		})
	}

	w := httptest.NewRecorder()
	r := httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/settings/restore", nil)
	redirectSettings(w, r, "backups", "", "", "This backup is not a provider restore point.")
	if w.Code != http.StatusBadRequest || !strings.Contains(w.Body.String(), "This backup is not a provider restore point.") || w.Header().Get("Location") != "" {
		t.Fatalf("failed restore response = %d %q, Location=%q", w.Code, w.Body.String(), w.Header().Get("Location"))
	}
	w = httptest.NewRecorder()
	redirectSettings(w, r, "audit", "Audit retention saved.", "", "")
	if w.Code != http.StatusSeeOther || w.Header().Get("Location") != "/" {
		t.Fatalf("successful mutation response = %d, Location=%q", w.Code, w.Header().Get("Location"))
	}
}
